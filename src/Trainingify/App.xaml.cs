using System.Reflection;

namespace Trainingify;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var version = typeof(App).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion.Split('+', 2)[0]
			?? throw new InvalidOperationException("Assembly informational version is missing.");

		return new Window(new MainPage())
		{
			Title = $"{AppInfo.Current.Name} {version}"
		};
	}
}
