using System.Windows;
#if DEVFLOW
using LeXtudio.DevFlow.Agent.Core;
using LeXtudio.DevFlow.Agent.Wpf;
using Microsoft.Maui.DevFlow.Agent.Core;
#endif

namespace TestApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
#if DEVFLOW
        private WpfAgentService _devFlowService;
#endif

        public App()
        {
            //Dispatcher.Thread.CurrentUICulture = new System.Globalization.CultureInfo("ru");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
#if DEVFLOW
            _devFlowService = this.AddWpfDevFlowAgent(new AgentOptions
            {
                Port = GetAgentPort()
            });
#endif
        }

#if DEVFLOW
        private static int GetAgentPort()
        {
            var portValue = System.Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT");
            if (int.TryParse(portValue, out var parsedPort) && parsedPort > 0)
            {
                return parsedPort;
            }

            return DevFlowAgentPortResolver.GetPortFromAssemblyMetadata() ?? AgentOptions.DefaultPort;
        }
#endif
    }
}
