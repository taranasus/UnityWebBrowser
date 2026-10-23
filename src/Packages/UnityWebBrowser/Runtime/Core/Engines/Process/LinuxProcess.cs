// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license. See the LICENSE.md file for more details.

#if UNITY_STANDALONE_LINUX

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using VoltstroStudios.UnityWebBrowser.Logging;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    /// <summary>
    ///     Engine process handling on Linux.
    ///     <para>
    ///         This must not depend on outside programs. Games launched by Steam run inside the Steam Linux Runtime
    ///         container, which has neither <c>pgrep</c> nor <c>kill</c>: the old pgrep-based clean-up threw a
    ///         Win32Exception before it signalled anything, so an engine that UWB had given up on kept running.
    ///         Process ids come from /proc and signals are sent with libc's kill() instead.
    ///     </para>
    /// </summary>
    internal sealed class LinuxProcess : IProcess
    {
        /// <summary>
        ///     Chromium switch that makes the engine keep its (empty) password store in plain files.
        ///     <para>
        ///         By default Chromium asks the desktop's keyring (KWallet on KDE, the Secret Service on GNOME).
        ///         A keyring that does not answer blocks the engine's start-up for about 50 seconds (two 25 second
        ///         D-Bus timeouts), far past the engine start-up timeout, so the browser never becomes ready.
        ///         The engine stores no passwords, so it has no use for the keyring.
        ///     </para>
        /// </summary>
        internal const string PasswordStoreSwitch = "--password-store=basic";

        private const int SigTerm = 15;
        private const int SigKill = 9;

        /// <summary>
        ///     How long the engine gets to exit after SIGTERM before it is SIGKILLed.
        /// </summary>
        private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(2);

        private readonly System.Diagnostics.Process process;
        private readonly IWebBrowserLogger logger;

        public LinuxProcess(IWebBrowserLogger logger)
        {
            process = new System.Diagnostics.Process();
            this.logger = logger;
        }

        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;

        public void StartProcess(string executable, string workingDir, string arguments, DataReceivedEventHandler onLogEvent,
            DataReceivedEventHandler onErrorLogEvent)
        {
            arguments = AddPasswordStoreSwitch(arguments);

            ProcessStartInfo startInfo = new(executable, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir
            };

            process.StartInfo = startInfo;
            process.OutputDataReceived += onLogEvent;
            process.ErrorDataReceived += onErrorLogEvent;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public void KillProcess()
        {
            int engineId;
            try
            {
                engineId = process.Id;
            }
            catch (InvalidOperationException)
            {
                //The process was never started
                return;
            }

            try
            {
                //Collect the whole tree first: once a parent dies its children are re-parented and can't be found
                List<ProcStat> descendants = GetDescendants(engineId);

                foreach (ProcStat child in descendants)
                    SendSignal(child, SigTerm);

                //Once it has exited its id may belong to someone else, so only signal it while it is still ours
                if (!process.HasExited)
                    SendSignal(engineId, SigTerm);

                if (!process.WaitForExit((int)ExitGracePeriod.TotalMilliseconds))
                {
                    logger.Warn($"Engine process {engineId} did not exit within {ExitGracePeriod.TotalSeconds}s of SIGTERM, killing it.");
                    SendSignal(engineId, SigKill);
                }

                foreach (ProcStat child in descendants)
                {
                    if (IsStillRunning(child))
                        SendSignal(child, SigKill);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to stop the engine process tree, killing the engine process only. {ex}");
                try
                {
                    process.Kill();
                }
                catch (Exception killEx)
                {
                    logger.Error($"Failed to kill the engine process! {killEx}");
                }
            }
        }

        public void Dispose()
        {
            process.Dispose();
        }

        internal static string AddPasswordStoreSwitch(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return PasswordStoreSwitch;

            //Respect a password store someone has already picked
            if (arguments.Contains("--password-store"))
                return arguments;

            return $"{arguments} {PasswordStoreSwitch}";
        }

        #region /proc

        /// <summary>
        ///     The few fields of /proc/[pid]/stat we need. <see cref="StartTime" /> tells a process apart from a
        ///     later one that reuses its id.
        /// </summary>
        internal readonly struct ProcStat
        {
            public ProcStat(int id, int parentId, char state, ulong startTime)
            {
                Id = id;
                ParentId = parentId;
                State = state;
                StartTime = startTime;
            }

            public int Id { get; }
            public int ParentId { get; }
            public char State { get; }
            public ulong StartTime { get; }
        }

        /// <summary>
        ///     Every process below <paramref name="rootId" />, children before grandchildren.
        /// </summary>
        internal static List<ProcStat> GetDescendants(int rootId)
        {
            Dictionary<int, List<ProcStat>> childrenByParent = new();
            foreach (string dir in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    continue;

                if (!TryReadStat(id, out ProcStat stat))
                    continue;

                if (!childrenByParent.TryGetValue(stat.ParentId, out List<ProcStat> children))
                {
                    children = new List<ProcStat>();
                    childrenByParent.Add(stat.ParentId, children);
                }

                children.Add(stat);
            }

            List<ProcStat> descendants = new();
            HashSet<int> seen = new() { rootId };
            Queue<int> toVisit = new();
            toVisit.Enqueue(rootId);
            while (toVisit.Count > 0)
            {
                if (!childrenByParent.TryGetValue(toVisit.Dequeue(), out List<ProcStat> children))
                    continue;

                foreach (ProcStat child in children)
                {
                    if (!seen.Add(child.Id))
                        continue;

                    descendants.Add(child);
                    toVisit.Enqueue(child.Id);
                }
            }

            return descendants;
        }

        internal static bool TryReadStat(int id, out ProcStat stat)
        {
            stat = default;
            string text;
            try
            {
                text = File.ReadAllText($"/proc/{id}/stat");
            }
            catch (Exception)
            {
                //It exited while we were looking, or we may not read it
                return false;
            }

            return TryParseStat(text, out stat);
        }

        /// <summary>
        ///     Parses a /proc/[pid]/stat line. The process name (field 2) is in brackets and may itself hold spaces
        ///     and brackets, so the fields are counted from the last ')'.
        /// </summary>
        internal static bool TryParseStat(string text, out ProcStat stat)
        {
            stat = default;
            if (string.IsNullOrEmpty(text))
                return false;

            int nameStart = text.IndexOf('(');
            int nameEnd = text.LastIndexOf(')');
            if (nameStart <= 0 || nameEnd < nameStart || nameEnd + 2 >= text.Length)
                return false;

            if (!int.TryParse(text.Substring(0, nameStart).Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out int id))
                return false;

            //Fields 3 onwards: state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt cmajflt utime stime
            //cutime cstime priority nice num_threads itrealvalue starttime ...
            string[] fields = text.Substring(nameEnd + 2).Split(' ');
            if (fields.Length < 20 || fields[0].Length != 1)
                return false;

            if (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId))
                return false;

            if (!ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startTime))
                return false;

            stat = new ProcStat(id, parentId, fields[0][0], startTime);
            return true;
        }

        private static bool IsStillRunning(ProcStat original)
        {
            //Gone, a zombie waiting to be reaped, or a new process that reuses the id
            return TryReadStat(original.Id, out ProcStat now)
                   && now.State != 'Z' && now.State != 'X'
                   && now.StartTime == original.StartTime;
        }

        #endregion

        #region Signals

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SysKill(int pid, int signal);

        private void SendSignal(ProcStat target, int signal)
        {
            //Don't signal a process that has gone and whose id now belongs to something else
            if (!IsStillRunning(target))
                return;

            SendSignal(target.Id, signal);
        }

        private void SendSignal(int processId, int signal)
        {
            try
            {
                if (SysKill(processId, signal) != 0)
                    logger.Debug($"kill({processId}, {signal}) failed with errno {Marshal.GetLastWin32Error()}.");
                return;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                logger.Warn($"Could not call libc kill(), falling back to Process.Kill. {ex.Message}");
            }

            //Process.Kill is always SIGKILL
            try
            {
                using System.Diagnostics.Process target = System.Diagnostics.Process.GetProcessById(processId);
                target.Kill();
            }
            catch (ArgumentException)
            {
                //Already gone
            }
            catch (InvalidOperationException)
            {
                //Already gone
            }
        }

        #endregion
    }
}

#endif
