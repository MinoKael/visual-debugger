#if TOOLS
#nullable enable
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// O plugin do editor. Ao ativar, registra o autoload <c>VisualDebug</c>; a cada abertura do editor,
	/// mostra as opções em Configurações do Projeto → visual_debug. O caminho do autoload sai da pasta
	/// deste script, então a pasta do addon pode morar em qualquer lugar do projeto.
	/// </summary>
	[Tool]
	public partial class VisualDebugPlugin : EditorPlugin
	{
		private const string AutoloadName = "VisualDebug";

		public override void _EnterTree() => DebugSettings.Register();

		public override void _EnablePlugin()
		{
			var folder = GetScript().As<Script>().ResourcePath.GetBaseDir();
			AddAutoloadSingleton(AutoloadName, folder.PathJoin(nameof(VisualDebugger) + ".cs"));
		}

		public override void _DisablePlugin() => RemoveAutoloadSingleton(AutoloadName);
	}
}
#endif
