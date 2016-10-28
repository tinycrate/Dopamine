﻿using Dopamine.Common.Services.Command;
using Dopamine.Common.Services.File;
using Dopamine.Core.Base;
using Dopamine.Core.Database;
using Dopamine.Core.IO;
using Dopamine.Core.Logging;
using Dopamine.Core.Settings;
using Dopamine.Views;
using System;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Dopamine
{
    public partial class App : Application
    {
        #region Variables
        private Mutex instanceMutex = null;
        #endregion

        #region Functions
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Create a jumplist and assign it to the current application
            JumpList.SetJumpList(Application.Current, new JumpList());

            // Check that there is only one instance of the application running
            bool isNewInstance = false;
            instanceMutex = new Mutex(true, string.Format("{0}-{1}", ProductInformation.ApplicationGuid, ProductInformation.AssemblyVersion.ToString()), out isNewInstance);

            // Process the commandline arguments
            this.ProcessCommandLineArguments(isNewInstance);

            if (isNewInstance)
            {
                instanceMutex.ReleaseMutex();
                this.ExecuteStartup();
            }
            else
            {
                LogClient.Instance.Logger.Warn("Dopamine is already running. Shutting down.");

                this.Shutdown();
            }
        }

        private void ExecuteStartup()
        {
            LogClient.Instance.Logger.Info("### STARTING {0}, version {1}, IsPortable = {2}, Windows version = {3} ###", ProductInformation.ApplicationDisplayName, ProductInformation.FormattedAssemblyVersion, XmlSettingsClient.Instance.Get<bool>("Application", "IsPortable"), Environment.OSVersion.VersionString);

            // Handler for unhandled AppDomain exceptions
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            // Show the Splash Window
            Window splashWin = new Splash();
            splashWin.Show();
        }

        private void ProcessCommandLineArguments(bool isNewInstance)
        {
            // Get the commandline arguments
            string[] args = Environment.GetCommandLineArgs();


            if (args.Length > 1)
            {
                LogClient.Instance.Logger.Info("Found commandline arguments.");

                switch (args[1])
                {
                    case "/donate":
                        LogClient.Instance.Logger.Info("Detected DonateCommand from JumpList.");

                        try
                        {
                            Actions.TryOpenLink(args[2]);
                        }
                        catch (Exception ex)
                        {
                            LogClient.Instance.Logger.Error("Could not open the link {0} in Internet Explorer. Exception: {1}", args[2], ex.Message);
                        }
                        this.Shutdown();
                        break;
                    default:

                        LogClient.Instance.Logger.Info("Processing Non-JumpList commandline arguments.");


                        if (!isNewInstance)
                        {
                            // Send the commandline arguments to the running instance
                            this.TrySendCommandlineArguments(args);
                        }
                        else
                        {
                            // Do nothing. The commandline arguments of a single instance will be processed,
                            // in the ShellViewModel because over there we have access to the FileService.
                        }
                        break;
                }
            }
            else
            {
                // When started without command line arguments, and when not the first instance: try to show the running instance.
                if (!isNewInstance) this.TryShowRunningInstance();
            }
        }


        private void TryShowRunningInstance()
        {
            ICommandService commandServiceProxy = default(ICommandService);
            ChannelFactory<ICommandService> commandServiceFactory = new ChannelFactory<ICommandService>(new StrongNetNamedPipeBinding(), new EndpointAddress(string.Format("net.pipe://localhost/{0}/CommandService/CommandServiceEndpoint", ProductInformation.ApplicationDisplayName)));

            try
            {
                commandServiceProxy = commandServiceFactory.CreateChannel();
                commandServiceProxy.ShowMainWindowCommand();
                LogClient.Instance.Logger.Info("Trying to show the running instance");
            }
            catch (Exception ex)
            {
                LogClient.Instance.Logger.Error("A problem occured while trying to show the running instance. Exception: {0}", ex.Message);
            }
        }


        private void TrySendCommandlineArguments(string[] args)
        {
            LogClient.Instance.Logger.Info("Trying to send {0} commandline arguments to the running instance", args.Count());

            bool needsSending = true;
            DateTime startTime = DateTime.Now;

            IFileService fileServiceProxy = default(IFileService);
            ChannelFactory<IFileService> fileServiceFactory = new ChannelFactory<IFileService>(new StrongNetNamedPipeBinding(), new EndpointAddress(string.Format("net.pipe://localhost/{0}/FileService/FileServiceEndpoint", ProductInformation.ApplicationDisplayName)));


            while (needsSending)
            {
                try
                {
                    // Try to send the commandline arguments to the running instance
                    fileServiceProxy = fileServiceFactory.CreateChannel();
                    fileServiceProxy.ProcessArguments(args);
                    LogClient.Instance.Logger.Info("Sent {0} commandline arguments to the running instance", args.Count());

                    needsSending = false;
                }
                catch (Exception ex)
                {

                    if (ex is EndpointNotFoundException)
                    {
                        // When selecting multiple files, the first file is opened by the first instance.
                        // This instance takes some time to start. To avoid an EndpointNotFoundException
                        // when sending the second file to the first instance, we wait 10 ms repetitively,
                        // until there is an endpoint to talk to.
                        System.Threading.Thread.Sleep(10);
                    }
                    else
                    {
                        // Log any other Exception and stop trying to send the file to the running instance
                        needsSending = false;
                        LogClient.Instance.Logger.Info("A problem occured while trying to send {0} commandline arguments to the running instance. Exception: {1}", args.Count(), ex.Message);
                    }
                }

                // This makes sure we don't try to send for longer than 30 seconds, 
                // so this instance won't stay open forever.
                if (Convert.ToInt64(DateTime.Now.Subtract(startTime).TotalSeconds) > 30)
                {
                    needsSending = false;
                }
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;

            // Log the exception and stop the application
            this.ExecuteEmergencyStop(ex);
        }


        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Prevent default unhandled exception processing
            e.Handled = true;

            // Log the exception and stop the application
            this.ExecuteEmergencyStop(e.Exception);
        }


        private void ExecuteEmergencyStop(Exception ex)
        {
            // This is a workaround for a bug in the .Net framework, which randomly causes a System.ArgumentNullException when
            // scrolling through a Virtualizing StackPanel. Scroll to playing song sometimes triggers this bug. We catch the
            // Exception here, and do nothing with it. The application can just proceed. This prevents a complete crash.
            // This might be fixed in .Net 4.5.2. See here: https://connect.microsoft.com/VisualStudio/feedback/details/789438/scrolling-in-virtualized-wpf-treeview-is-very-unstable
            if (ex.GetType().ToString().Equals("System.ArgumentNullException") & ex.Source.ToString().Equals("PresentationCore"))
            {
                LogClient.Instance.Logger.Warn("Avoided Unhandled Exception: {0}", ex.ToString());
                return;
            }

            LogClient.Instance.Logger.Error("Unhandled Exception. {0}", LogClient.GetAllExceptions(ex));

            // Close the application to prevent further problems
            LogClient.Instance.Logger.Info("### FORCED STOP of {0}, version {1} ###", ProductInformation.ApplicationDisplayName, ProductInformation.FormattedAssemblyVersion);

            // Emergency save of the settings
            XmlSettingsClient.Instance.Write();

            Application.Current.Shutdown();
        }
        #endregion
    }
}
