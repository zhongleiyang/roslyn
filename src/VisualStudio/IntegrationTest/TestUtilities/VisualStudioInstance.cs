﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnvDTE;
using Microsoft.VisualStudio.IntegrationTest.Utilities.InProcess;
using Microsoft.VisualStudio.IntegrationTest.Utilities.Input;
using Microsoft.VisualStudio.IntegrationTest.Utilities.OutOfProcess;

using Process = System.Diagnostics.Process;

namespace Microsoft.VisualStudio.IntegrationTest.Utilities
{
    public class VisualStudioInstance
    {
        private readonly IntegrationService _integrationService;
        private readonly IpcClientChannel _integrationServiceChannel;
        private readonly VisualStudio_InProc _inProc;

        public SendKeys SendKeys { get; }

        public ChangeSignatureDialog_OutOfProc ChangeSignatureDialog { get; }

        public CSharpInteractiveWindow_OutOfProc CSharpInteractiveWindow { get; }

        public Editor_OutOfProc Editor { get; }

        public FindReferencesWindow_OutOfProc FindReferencesWindow { get; }

        public GenerateTypeDialog_OutOfProc GenerateTypeDialog { get; }

        public Shell_OutOfProc Shell { get; }

        public SolutionExplorer_OutOfProc SolutionExplorer { get; }

        public VisualStudioWorkspace_OutOfProc VisualStudioWorkspace { get; }

        internal DTE Dte { get; }

        internal Process HostProcess { get; }

        /// <summary>
        /// The set of Visual Studio packages that are installed into this instance.
        /// </summary>
        public ImmutableHashSet<string> SupportedPackageIds { get; }

        /// <summary>
        /// The path to the root of this installed version of Visual Studio. This is the folder that contains
        /// Common7\IDE.
        /// </summary>
        public string InstallationPath { get; }

        public VisualStudioInstance(Process hostProcess, DTE dte, ImmutableHashSet<string> supportedPackageIds, string installationPath)
        {
            HostProcess = hostProcess;
            Dte = dte;
            SupportedPackageIds = supportedPackageIds;
            InstallationPath = installationPath;

            StartRemoteIntegrationService(dte);

            _integrationServiceChannel = new IpcClientChannel($"IPC channel client for {HostProcess.Id}", sinkProvider: null);
            ChannelServices.RegisterChannel(_integrationServiceChannel, ensureSecurity: true);

            // Connect to a 'well defined, shouldn't conflict' IPC channel
            _integrationService = IntegrationService.GetInstanceFromHostProcess(hostProcess);

            // Create marshal-by-ref object that runs in host-process.
            _inProc = ExecuteInHostProcess<VisualStudio_InProc>(
                type: typeof(VisualStudio_InProc),
                methodName: nameof(VisualStudio_InProc.Create)
            );

            // There is a lot of VS initialization code that goes on, so we want to wait for that to 'settle' before
            // we start executing any actual code.
            _inProc.WaitForSystemIdle();

            ChangeSignatureDialog = new ChangeSignatureDialog_OutOfProc(this);
            CSharpInteractiveWindow = new CSharpInteractiveWindow_OutOfProc(this);
            Editor = new Editor_OutOfProc(this);
            FindReferencesWindow = new FindReferencesWindow_OutOfProc(this);
            GenerateTypeDialog = new GenerateTypeDialog_OutOfProc(this);
            Shell = new Shell_OutOfProc(this);
            SolutionExplorer = new SolutionExplorer_OutOfProc(this);
            VisualStudioWorkspace = new VisualStudioWorkspace_OutOfProc(this);

            SendKeys = new SendKeys(this);

            // Ensure we are in a known 'good' state by cleaning up anything changed by the previous instance
            CleanUp();
        }

        public void ExecuteInHostProcess(Type type, string methodName)
        {
            var result = _integrationService.Execute(type.Assembly.Location, type.FullName, methodName);

            if (result != null)
            {
                throw new InvalidOperationException("The specified call was not expected to return a value.");
            }
        }

        public T ExecuteInHostProcess<T>(Type type, string methodName)
        {
            var objectUri = _integrationService.Execute(type.Assembly.Location, type.FullName, methodName) ?? throw new InvalidOperationException("The specified call was expected to return a value.");
            return (T)Activator.GetObject(typeof(T), $"{_integrationService.BaseUri}/{objectUri}");
        }

        public void ActivateMainWindow()
            => _inProc.ActivateMainWindow();

        public void WaitForApplicationIdle()
            => _inProc.WaitForApplicationIdle();

        public void ExecuteCommand(string commandName, string argument = "")
            => _inProc.ExecuteCommand(commandName, argument);

        public bool IsRunning => !HostProcess.HasExited;

        public async Task ClickAutomationElementAsync(string elementName, bool recursive = false)
        {
            var element = await FindAutomationElementAsync(elementName, recursive).ConfigureAwait(false);

            if (element != null)
            {
                var tcs = new TaskCompletionSource<object>();

                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, element, TreeScope.Element, (src, e) => {
                    tcs.SetResult(null);
                });

                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePatternObj))
                {
                    var invokePattern = (InvokePattern)invokePatternObj;
                    invokePattern.Invoke();
                }

                await tcs.Task;
            }
        }

        private async Task<AutomationElement> FindAutomationElementAsync(string elementName, bool recursive = false)
        {
            AutomationElement element = null;
            var scope = recursive ? TreeScope.Descendants : TreeScope.Children;
            var condition = new PropertyCondition(AutomationElement.NameProperty, elementName);

            // TODO(Dustin): This is code is a bit terrifying. If anything goes wrong and the automation
            // element can't be found, it'll continue to spin until the heat death of the universe.
            await IntegrationHelper.WaitForResultAsync(
                () => (element = AutomationElement.RootElement.FindFirst(scope, condition)) != null, expectedResult: true
            ).ConfigureAwait(false);

            return element;
        }

        public void CleanUp()
        {
            VisualStudioWorkspace.CleanUpWaitingService();
            VisualStudioWorkspace.CleanUpWorkspace();
            SolutionExplorer.CleanUpOpenSolution();
            CSharpInteractiveWindow.CloseInteractiveWindow();
        }

        public void Close(bool exitHostProcess = true)
        {
            if (!IsRunning)
            {
                return;
            }

            CleanUp();

            CloseRemotingService();

            if (exitHostProcess)
            {
                CloseHostProcess();
            }
        }

        private void CloseHostProcess()
        {
            _inProc.Quit();
            IntegrationHelper.KillProcess(HostProcess);
        }

        private void CloseRemotingService()
        {
            try
            {
                StopRemoteIntegrationService();
            }
            finally
            {
                if (_integrationServiceChannel != null)
                {
                    ChannelServices.UnregisterChannel(_integrationServiceChannel);
                }
            }
        }

        private void StartRemoteIntegrationService(DTE dte)
        {
            // We use DTE over RPC to start the integration service. All other DTE calls should happen in the host process.

            if (RetryRpcCall(() => dte.Commands.Item(WellKnownCommandNames.Test_IntegrationTestService_Start).IsAvailable))
            {
                RetryRpcCall(() => dte.ExecuteCommand(WellKnownCommandNames.Test_IntegrationTestService_Start));
            }
        }

        private void StopRemoteIntegrationService()
        {
            if (_inProc.IsCommandAvailable(WellKnownCommandNames.Test_IntegrationTestService_Stop))
            {
                _inProc.ExecuteCommand(WellKnownCommandNames.Test_IntegrationTestService_Stop);
            }
        }

        private static void RetryRpcCall(Action action)
        {
            do
            {
                try
                {
                    action();
                    return;
                }
                catch (COMException exception) when ((exception.HResult == VSConstants.RPC_E_CALL_REJECTED) ||
                                                     (exception.HResult == VSConstants.RPC_E_SERVERCALL_RETRYLATER))
                {
                    // We'll just try again in this case
                }
            }
            while (true);
        }

        private static T RetryRpcCall<T>(Func<T> action)
        {
            var result = default(T);

            RetryRpcCall(() => {
                result = action();
            });

            return result;
        }
    }
}
