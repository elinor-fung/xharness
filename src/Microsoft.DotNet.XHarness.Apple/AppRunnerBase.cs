﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.XHarness.Common.Execution;
using Microsoft.DotNet.XHarness.Common.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared;
using Microsoft.DotNet.XHarness.iOS.Shared.Hardware;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;

namespace Microsoft.DotNet.XHarness.Apple
{
    public abstract class AppRunnerBase
    {
        private const string SystemLogPath = "/var/log/system.log";

        private readonly IFileBackedLog _mainLog;
        private readonly IHardwareDeviceLoader _hardwareDeviceLoader;
        private readonly ICaptureLogFactory _captureLogFactory;
        private readonly ILogs _logs;
        private readonly IProcessManager _processManager;

        protected AppRunnerBase(
            IProcessManager processManager,
            IHardwareDeviceLoader hardwareDeviceLoader,
            ICaptureLogFactory captureLogFactory,
            ILogs logs,
            IFileBackedLog mainLog,
            Action<string>? logCallback = null)
        {
            _hardwareDeviceLoader = hardwareDeviceLoader ?? throw new ArgumentNullException(nameof(hardwareDeviceLoader));
            _captureLogFactory = captureLogFactory ?? throw new ArgumentNullException(nameof(captureLogFactory));
            _logs = logs ?? throw new ArgumentNullException(nameof(logs));
            _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));

            if (logCallback == null)
            {
                _mainLog = mainLog ?? throw new ArgumentNullException(nameof(mainLog));
            }
            else
            {
                // create using the main as the default log
                _mainLog = Log.CreateReadableAggregatedLog(mainLog, new CallbackLog(logCallback));
            }
        }

        protected async Task<string> FindDevice(TestTargetOs target)
        {
            IHardwareDevice? companionDevice = null;
            IHardwareDevice device = await _hardwareDeviceLoader.FindDevice(target.Platform.ToRunMode(), _mainLog, includeLocked: false, force: false);

            if (target.Platform.IsWatchOSTarget())
            {
                companionDevice = await _hardwareDeviceLoader.FindCompanionDevice(_mainLog, device);
            }

            return companionDevice?.Name ?? device.Name;
        }

        protected async Task<ProcessExecutionResult> RunMacCatalystApp(
            AppBundleInformation appInfo,
            TimeSpan timeout,
            IEnumerable<string> extraArguments,
            Dictionary<string, string> environmentVariables,
            CancellationToken cancellationToken)
        {
            using var systemLog = _captureLogFactory.Create(
                path: _logs.CreateFile("MacCatalyst.system.log", LogType.SystemLog),
                systemLogPath: SystemLogPath,
                entireFile: false,
                LogType.SystemLog);

            // We need to make the binary executable
            var binaryPath = Path.Combine(appInfo.AppPath, "Contents", "MacOS", appInfo.BundleExecutable ?? appInfo.AppName);
            if (File.Exists(binaryPath))
            {
                await _processManager.ExecuteCommandAsync("chmod", new[] { "+x", binaryPath }, _mainLog, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            }

            var arguments = new List<string>
            {
                "-W",
                appInfo.LaunchAppPath
            };

            arguments.AddRange(extraArguments);

            systemLog.StartCapture();

            try
            {
                return await _processManager.ExecuteCommandAsync(
                    "open",
                    arguments,
                    _mainLog,
                    timeout,
                    environmentVariables,
                    cancellationToken);
            }
            finally
            {
                systemLog.StopCapture(waitIfEmpty: TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// User can pass additional arguments after the -- which get turned to environmental variables.
        /// </summary>
        /// <param name="envVariables">Environmental variables where the arguments are added</param>
        /// <param name="variables">Variables to set</param>
        protected void AddExtraEnvVars(Dictionary<string, string> envVariables, IEnumerable<(string, string)> variables)
        {
            using (var enumerator = variables.GetEnumerator())
            while (enumerator.MoveNext())
            {
                var (name, value) = enumerator.Current;
                if (envVariables.ContainsKey(name))
                {
                    _mainLog.WriteLine($"Environmental variable {name} is already passed to the application to drive test run, skipping..");
                    continue;
                }

                envVariables[name] = value;
            }
        }
    }
}
