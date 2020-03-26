﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * IBAutomater v1.0. Copyright 2019 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace QuantConnect.IBAutomater
{
    /// <summary>
    /// The IB Automater is responsible for automating the configuration/logon process
    /// to the IB Gateway application and for handling/dismissing its popup windows.
    /// </summary>
    public class IBAutomater
    {
        private readonly string _ibDirectory;
        private readonly string _ibVersion;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _tradingMode;
        private readonly int _portNumber;

        private readonly object _locker = new object();
        private Process _process;
        private StartResult _lastStartResult = StartResult.Success;
        private readonly AutoResetEvent _ibAutomaterInitializeEvent = new AutoResetEvent(false);
        private bool _twoFactorConfirmationPending;

        /// <summary>
        /// Event fired when the process writes to the output stream
        /// </summary>
        public event EventHandler<OutputDataReceivedEventArgs> OutputDataReceived;

        /// <summary>
        /// Event fired when the process writes to the error stream
        /// </summary>
        public event EventHandler<ErrorDataReceivedEventArgs> ErrorDataReceived;

        /// <summary>
        /// Event fired when the process exits
        /// </summary>
        public event EventHandler<ExitedEventArgs> Exited;

        /// <summary>
        /// Main program for testing and/or standalone execution
        /// </summary>
        public static void Main(string[] args)
        {
            var json = File.ReadAllText("config.json");
            var config = JObject.Parse(json);

            var ibDirectory = config["ib-tws-dir"].ToString();
            var userName = config["ib-user-name"].ToString();
            var password = config["ib-password"].ToString();
            var tradingMode = config["ib-trading-mode"].ToString();
            var portNumber = config["ib-port"].ToObject<int>();
            var ibVersion = "974";
            if (config["ib-version"] != null)
            {
                ibVersion = config["ib-version"].ToString();
            }

            // Create a new instance of the IBAutomater class
            var automater = new IBAutomater(ibDirectory, ibVersion, userName, password, tradingMode, portNumber);

            // Attach the event handlers
            automater.OutputDataReceived += (s, e) => Console.WriteLine($"{DateTime.UtcNow:O} {e.Data}");
            automater.ErrorDataReceived += (s, e) => Console.WriteLine($"{DateTime.UtcNow:O} {e.Data}");
            automater.Exited += (s, e) => Console.WriteLine($"{DateTime.UtcNow:O} IBAutomater exited [ExitCode:{e.ExitCode}]");

            // Start the IBAutomater
            Console.WriteLine("===> Starting IBAutomater");
            var result = automater.Start(false);
            if (result.HasError)
            {
                Console.WriteLine($"Failed to start IBAutomater - Code: {result.ErrorCode}, Message: {result.ErrorMessage}");
                automater.Stop();
                return;
            }

            // Restart the IBAutomater
            Console.WriteLine("===> Restarting IBAutomater");
            result = automater.Restart();
            if (result.HasError)
            {
                Console.WriteLine($"Failed to restart IBAutomater - Code: {result.ErrorCode}, Message: {result.ErrorMessage}");
                automater.Stop();
                return;
            }

            // Stop the IBAutomater
            Console.WriteLine("===> Stopping IBAutomater");
            automater.Stop();
            Console.WriteLine("IBAutomater stopped");
        }

        /// <summary>
        /// Creates a new instance of the <see cref="IBAutomater"/> class
        /// </summary>
        /// <param name="ibDirectory">The root directory of IB Gateway</param>
        /// <param name="ibVersion">The IB Gateway version to launch</param>
        /// <param name="userName">The user name</param>
        /// <param name="password">The password</param>
        /// <param name="tradingMode">The trading mode ('paper' or 'live')</param>
        /// <param name="portNumber">The API port number</param>
        public IBAutomater(string ibDirectory, string ibVersion, string userName, string password, string tradingMode, int portNumber)
        {
            _ibDirectory = ibDirectory;
            _ibVersion = ibVersion;
            _userName = userName;
            _password = password;
            _tradingMode = tradingMode;
            _portNumber = portNumber;
        }

        /// <summary>
        /// Starts the IB Gateway
        /// </summary>
        /// <param name="waitForExit">true if it should wait for the IB Gateway process to exit</param>
        /// <remarks>The IB Gateway application will be launched</remarks>
        public StartResult Start(bool waitForExit)
        {
            lock (_locker)
            {
                if (_lastStartResult.HasError)
                {
                    // IBAutomater errors are unrecoverable
                    return _lastStartResult;
                }

                if (IsRunning())
                {
                    return StartResult.Success;
                }

                _process = null;
                _ibAutomaterInitializeEvent.Reset();

                if (IsLinux)
                {
                    // need permission for execution
                    OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs("Setting execute permissions on IBAutomater.sh"));
                    ExecuteProcessAndWaitForExit("chmod", "+x IBAutomater.sh");
                }

                var fileName = IsWindows ? "IBAutomater.bat" : "IBAutomater.sh";
                var arguments = $"{_ibDirectory} {_ibVersion} {_userName} {_password} {_tradingMode} {_portNumber}";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(fileName, arguments)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs(e.Data));

                        // login failed
                        if (e.Data.Contains("Login failed"))
                        {
                            _lastStartResult = new StartResult(ErrorCode.LoginFailed);
                            _ibAutomaterInitializeEvent.Set();
                        }

                        // an existing session was detected
                        else if (e.Data.Contains("Existing session detected"))
                        {
                            _lastStartResult = new StartResult(ErrorCode.ExistingSessionDetected);
                            _ibAutomaterInitializeEvent.Set();
                        }

                        // a security dialog (2FA) was detected by IBAutomater
                        else if (e.Data.Contains("Second Factor Authentication"))
                        {
                            if (e.Data.Contains("[WINDOW_OPENED]"))
                            {
                                // waiting for 2FA confirmation on IBKR mobile app
                                const string message = "Waiting for 2FA confirmation on IBKR mobile app (to be confirmed within 3 minutes).";
                                OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs(message));

                                _twoFactorConfirmationPending = true;
                            }
                        }

                        // a security dialog (code card) was detected by IBAutomater
                        else if (e.Data.Contains("Security Code Card Authentication") ||
                                 e.Data.Contains("Enter security code"))
                        {
                            _lastStartResult = new StartResult(ErrorCode.SecurityDialogDetected);
                            _ibAutomaterInitializeEvent.Set();
                        }

                        // initialization completed
                        else if (e.Data.Contains("Configuration settings updated"))
                        {
                            _ibAutomaterInitializeEvent.Set();
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        ErrorDataReceived?.Invoke(this, new ErrorDataReceivedEventArgs(e.Data));
                    }
                };

                process.Exited += (sender, e) =>
                {
                    Exited?.Invoke(this, new ExitedEventArgs(process.ExitCode));
                };

                try
                {
                    var started = process.Start();
                    if (!started)
                    {
                        return new StartResult(ErrorCode.ProcessStartFailed);
                    }
                }
                catch (Exception exception)
                {
                    return new StartResult(ErrorCode.ProcessStartFailed, exception.Message);
                }

                OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs($"IBAutomater process started - Id:{process.Id}"));

                _process = process;

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                if (waitForExit)
                {
                    process.WaitForExit();
                }
                else
                {
                    // wait for completion of IBGateway login and configuration
                    string message;
                    if (_ibAutomaterInitializeEvent.WaitOne(TimeSpan.FromSeconds(60)))
                    {
                        message = "IB Automater initialized.";
                    }
                    else
                    {
                        if (_twoFactorConfirmationPending)
                        {
                            // wait for completion of two-factor authentication
                            if (!_ibAutomaterInitializeEvent.WaitOne(TimeSpan.FromMinutes(3)))
                            {
                                _lastStartResult = new StartResult(ErrorCode.TwoFactorConfirmationTimeout);
                                message = "IB Automater 2FA timeout.";
                            }
                            else
                            {
                                // 2FA confirmation successful
                                message = "IB Automater initialized.";
                            }
                        }
                        else
                        {
                            message = "IB Automater initialization timeout.";
                        }
                    }

                    OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs(message));

                    // reset the flag, this method is called multiple times
                    _twoFactorConfirmationPending = false;

                    if (_lastStartResult.HasError)
                    {
                        message = $"IBAutomater error - Code: {_lastStartResult.ErrorCode} Message: {_lastStartResult.ErrorMessage}";
                        OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs(message));

                        return _lastStartResult;
                    }
                }
            }

            return StartResult.Success;
        }

        /// <summary>
        /// Stops the IB Gateway
        /// </summary>
        /// <remarks>The IB Gateway application will be terminated</remarks>
        public void Stop()
        {
            lock (_locker)
            {
                if (!IsRunning())
                {
                    return;
                }

                if (IsWindows)
                {
                    foreach (var process in Process.GetProcesses())
                    {
                        try
                        {
                            if (process.MainWindowTitle.ToLower().Contains("ib gateway"))
                            {
                                process.Kill();
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                }
                else
                {
                    try
                    {
                        Process.Start("pkill", "xvfb-run");
                        Process.Start("pkill", "java");
                        Process.Start("pkill", "Xvfb");
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }

                _process = null;
            }
        }

        /// <summary>
        /// Restarts the IB Gateway
        /// </summary>
        /// <remarks>The IB Gateway application will be restarted</remarks>
        public StartResult Restart()
        {
            lock (_locker)
            {
                Stop();

                Thread.Sleep(2500);

                return Start(false);
            }
        }

        /// <summary>
        /// Gets the last <see cref="StartResult"/> instance
        /// </summary>
        /// <returns>Returns the last start result instance</returns>
        public StartResult GetLastStartResult()
        {
            return _lastStartResult;
        }

        /// <summary>
        /// Returns whether the IBGateway is running
        /// </summary>
        /// <returns>true if the IBGateway is running</returns>
        private bool IsRunning()
        {
            lock (_locker)
            {
                if (_process == null)
                {
                    return false;
                }

                var exited = _process.HasExited;
                if (exited)
                {
                    _process = null;
                }

                return !exited;
            }
        }

        private void ExecuteProcessAndWaitForExit(string fileName, string arguments)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            p.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs($"{fileName}: {e.Data}"));
                }
            };
            p.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    ErrorDataReceived?.Invoke(this, new ErrorDataReceivedEventArgs($"{fileName}: {e.Data}"));
                }
            };

            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            p.WaitForExit();

            OutputDataReceived?.Invoke(this, new OutputDataReceivedEventArgs($"{fileName} {arguments}: process exit code: {p.ExitCode}"));
        }

        private static bool IsLinux
        {
            get
            {
                var p = (int)Environment.OSVersion.Platform;
                return p == 4 || p == 6 || p == 128;
            }
        }

        private static bool IsWindows => !IsLinux;
    }
}
