﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.XHarness.Android;
using Microsoft.DotNet.XHarness.Android.Execution;
using Microsoft.DotNet.XHarness.CLI.CommandArguments;
using Microsoft.DotNet.XHarness.CLI.CommandArguments.Android;
using Microsoft.DotNet.XHarness.Common.CLI;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.XHarness.CLI.Commands.Android
{
    internal class AndroidTestCommand : TestCommand
    {
        // nunit2 one should go away eventually
        private static readonly string[] s_xmlOutputVariableNames = { "nunit2-results-path", "test-results-path" };
        private const string TestRunSummaryVariableName = "test-execution-summary";
        private const string ShortMessageVariableName = "shortMsg";
        private const string ReturnCodeVariableName = "return-code";

        private readonly AndroidTestCommandArguments _arguments = new AndroidTestCommandArguments();

        protected override TestCommandArguments TestArguments => _arguments;

        protected override string CommandUsage { get; } = "android test [OPTIONS]";

        private const string CommandHelp = "Executes test .apk on an Android device, waits up to a given timeout, then copies files off the device and uninstalls the test app";
        protected override string CommandDescription { get; } = @$"
{CommandHelp}

APKs can communicate status back to XHarness using the results bundle:

Required:
{ReturnCodeVariableName} - Exit code for instrumentation. Necessary because a crashing instrumentation may be indistinguishable from a passing one from exit codes.

Optional:
Test results Paths:
{string.Join('\n', s_xmlOutputVariableNames)} - If specified, this file will be copied off the device after execution (used for external reporting)
Reporting:
{TestRunSummaryVariableName},{ShortMessageVariableName} - If specified, this will be printed to the console directly after execution (useful for printing summaries)
 
Arguments:
";

        public AndroidTestCommand() : base(CommandHelp)
        {
        }

        protected override Task<ExitCode> InvokeInternal(ILogger logger)
        {
            logger.LogDebug($"Android Test command called: App = {_arguments.AppPackagePath}{Environment.NewLine}Instrumentation Name = {_arguments.InstrumentationName}");
            logger.LogDebug($"Output Directory:{_arguments.OutputDirectory}{Environment.NewLine}Timeout = {_arguments.Timeout.TotalSeconds} seconds.");
            logger.LogDebug("Arguments to instrumentation:");

            if (!File.Exists(_arguments.AppPackagePath))
            {
                logger.LogCritical($"Couldn't find {_arguments.AppPackagePath}!");
                return Task.FromResult(ExitCode.PACKAGE_NOT_FOUND);
            }
            var runner = new AdbRunner(logger);

            // Assumption: APKs we test will only have one arch for now
            string apkRequiredArchitecture;

            if (!string.IsNullOrEmpty(_arguments.DeviceArchitecture))
            {
                apkRequiredArchitecture = _arguments.DeviceArchitecture;
                logger.LogInformation($"Will attempt to run device on specified architecture: '{apkRequiredArchitecture}'");
            }
            else
            {
                apkRequiredArchitecture = ApkHelper.GetApkSupportedArchitectures(_arguments.AppPackagePath).First();
                logger.LogInformation($"Will attempt to run device on detected architecture: '{apkRequiredArchitecture}'");
            }

            // Package Name is not guaranteed to match file name, so it needs to be mandatory.
            string apkPackageName = _arguments.PackageName;

            int instrumentationExitCode = (int)ExitCode.GENERAL_FAILURE;

            try
            {
                using (logger.BeginScope("Initialization and setup of APK on device"))
                {
                    // Make sure the adb server is started
                    runner.StartAdbServer();

                    // enumerate the devices attached and their architectures
                    // Tell ADB to only use that one (will always use the present one for systems w/ only 1 machine)
                    runner.SetActiveDevice(GetDeviceToUse(logger, runner, apkRequiredArchitecture));

                    // Wait til at least device(s) are ready
                    runner.WaitForDevice();

                    // Empty log as we'll be uploading the full logcat for this execution
                    runner.ClearAdbLog();

                    logger.LogDebug($"Working with {runner.GetAdbVersion()}");

                    // If anything changed about the app, Install will fail; uninstall it first.
                    // (we'll ignore if it's not present)
                    // This is where mismatched architecture APKs fail.
                    runner.UninstallApk(apkPackageName);
                    if (runner.InstallApk(_arguments.AppPackagePath) != 0)
                    {
                        logger.LogCritical("Install failure: Test command cannot continue");
                        return Task.FromResult(ExitCode.PACKAGE_INSTALLATION_FAILURE);
                    }
                    runner.KillApk(apkPackageName);
                }

                // No class name = default Instrumentation
                ProcessExecutionResults? result = runner.RunApkInstrumentation(apkPackageName, _arguments.InstrumentationName, _arguments.InstrumentationArguments, _arguments.Timeout);

                using (logger.BeginScope("Post-test copy and cleanup"))
                {
                    if (result.ExitCode == (int)ExitCode.SUCCESS)
                    {
                        (var resultValues, var instrExitCode) = ParseInstrumentationOutputs(logger, result.StandardOutput);

                        // This is where test instrumentation can communicate outwardly that test execution failed
                        instrumentationExitCode = instrExitCode;

                        // Pull XUnit result XMLs off the device
                        foreach (string possibleResultKey in s_xmlOutputVariableNames)
                        {
                            if (resultValues.ContainsKey(possibleResultKey))
                            {
                                logger.LogInformation($"Found XML result file: '{resultValues[possibleResultKey]}'(key: {possibleResultKey})");
                                runner.PullFiles(resultValues[possibleResultKey], _arguments.OutputDirectory);
                            }
                        }
                        if (resultValues.ContainsKey(TestRunSummaryVariableName))
                        {
                            logger.LogInformation($"Test execution summary:{Environment.NewLine}{resultValues[TestRunSummaryVariableName]}");
                        }
                        if (resultValues.ContainsKey(ShortMessageVariableName))
                        {
                            logger.LogInformation($"Short Message: {Environment.NewLine}{resultValues[ShortMessageVariableName]}");
                        }

                        // Due to the particulars of how instrumentations work, ADB will report a 0 exit code for crashed instrumentations
                        // We'll change that to a specific value and print a message explaining why.
                        if (resultValues.ContainsKey(ReturnCodeVariableName))
                        {
                            if (int.TryParse(resultValues[ReturnCodeVariableName], out int bundleExitCode))
                            {
                                logger.LogInformation($"Instrumentation finished normally with exit code {bundleExitCode}");
                                instrumentationExitCode = bundleExitCode;
                            }
                            else
                            {
                                logger.LogError($"Un-parse-able value for '{ReturnCodeVariableName}' : '{resultValues[ReturnCodeVariableName]}'");
                                instrumentationExitCode = (int)ExitCode.RETURN_CODE_NOT_SET;
                            }
                        }
                        else
                        {
                            logger.LogError($"No value for '{ReturnCodeVariableName}' provided in instrumentation result.  This may indicate a crashed test (see log)");
                            instrumentationExitCode = (int)ExitCode.RETURN_CODE_NOT_SET;
                        }
                    }

                    // Optionally copy off an entire folder
                    if (!string.IsNullOrEmpty(_arguments.DeviceOutputFolder))
                    {
                        var logs = runner.PullFiles(_arguments.DeviceOutputFolder, _arguments.OutputDirectory);
                        foreach (string log in logs)
                        {
                            logger.LogDebug($"Found output file: {log}");
                        }
                    }
                    runner.DumpAdbLog(Path.Combine(_arguments.OutputDirectory, $"adb-logcat-{_arguments.PackageName}.log"));
                    runner.UninstallApk(apkPackageName);
                }

                if (instrumentationExitCode != _arguments.ExpectedExitCode)
                {
                    logger.LogError($"Non-success instrumentation exit code: {instrumentationExitCode}, expected: {_arguments.ExpectedExitCode}");
                }
                else
                {
                    return Task.FromResult(ExitCode.SUCCESS);
                }
            }
            catch (Exception toLog)
            {
                logger.LogCritical(toLog, $"Failure to run test package: {toLog.Message}");
            }

            return Task.FromResult(ExitCode.GENERAL_FAILURE);
        }

        private string? GetDeviceToUse(ILogger logger, AdbRunner runner, string apkRequiredArchitecture)
        {
            var allDevicesAndTheirArchitectures = runner.GetAttachedDevicesAndArchitectures();
            if (allDevicesAndTheirArchitectures.Count > 0)
            {
                if (allDevicesAndTheirArchitectures.Any(kvp => kvp.Value != null && kvp.Value.Equals(apkRequiredArchitecture, StringComparison.OrdinalIgnoreCase)))
                {
                    var firstAvailableCompatible = allDevicesAndTheirArchitectures.Where(kvp => kvp.Value != null && kvp.Value.Equals(apkRequiredArchitecture, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    logger.LogInformation($"Using first-found compatible device of {allDevicesAndTheirArchitectures.Count} total- serial: '{firstAvailableCompatible.Key}' - Arch: {firstAvailableCompatible.Value}");
                    return firstAvailableCompatible.Key;
                }
                else
                {
                    logger.LogWarning($"No devices found with architecture '{apkRequiredArchitecture}'.  Just returning first available device; installation will likely fail, but we'll try anyways.");
                    return allDevicesAndTheirArchitectures.Keys.First();
                }
            }
            logger.LogError("No attached device detected");
            return null;
        }

        private (Dictionary<string, string> values, int exitCode) ParseInstrumentationOutputs(ILogger logger, string stdOut)
        {
            // If ADB.exe's output changes (which we control when we take updates in this repo), we'll need to fix this.
            string resultPrefix = "INSTRUMENTATION_RESULT:";
            string exitCodePrefix = "INSTRUMENTATION_CODE:";
            int exitCode = -1;
            var outputs = new Dictionary<string, string>();
            string[] lines = stdOut.Split(Environment.NewLine);

            foreach (string line in lines)
            {
                if (line.StartsWith(resultPrefix))
                {
                    var subString = line.Substring(resultPrefix.Length);
                    string[] results = subString.Trim().Split('=');
                    if (results.Length == 2)
                    {
                        if (outputs.ContainsKey(results[0]))
                        {
                            logger.LogWarning($"Key '{results[0]}' defined more than once");
                            outputs[results[0]] = results[1];
                        }
                        else
                        {
                            outputs.Add(results[0], results[1]);
                        }
                    }
                    else
                    {
                        logger.LogWarning($"Skipping output line due to key-value-pair parse failure: '{line}'");
                    }
                }
                else if (line.StartsWith(exitCodePrefix))
                {
                    if (!int.TryParse(line.Substring(exitCodePrefix.Length).Trim(), out exitCode))
                    {
                        logger.LogError($"Failure parsing ADB Exit code from line: '{line}'");
                    }
                }
            }

            return (outputs, exitCode);
        }
    }
}
