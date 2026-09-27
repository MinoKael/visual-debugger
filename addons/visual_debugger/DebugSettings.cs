#nullable enable
using System;
using Godot;
using Godot.Collections;

namespace VisualDebug
{
	/// <summary>
	/// As opções do addon em Configurações do Projeto → visual_debug (visíveis com "Configurações
	/// avançadas"). O plugin as registra no editor, com dica e faixa; em jogo, cada leitura cai no padrão
	/// quando a opção não está no project.godot, então o autoload funciona até sem o plugin ativo.
	/// </summary>
	public static class DebugSettings
	{
		private const string ReleaseKey = "visual_debug/general/enabled_in_release";
		private const string StartLevelKey = "visual_debug/general/start_level";
		private const string FontSizeKey = "visual_debug/display/font_size";
		private const string InternalKey = "visual_debug/display/include_internal_nodes";
		private const string CaptureKey = "visual_debug/trace/capture_on_startup";

		private const int DefaultFontSize = 11;

		/// <summary>Em build de depuração sempre; em release, só se a opção pedir.</summary>
		public static bool Enabled => OS.IsDebugBuild() || Bool(ReleaseKey, false);

		/// <summary>A camada ligada ao abrir o jogo.</summary>
		public static DebugLevel StartLevel => (DebugLevel)Math.Clamp(Int(StartLevelKey, 0), 0, (int)DebugLevel.Trace);

		public static int FontSize => Math.Clamp(Int(FontSizeKey, DefaultFontSize), 6, 48);

		/// <summary>Inclui os filhos internos dos controles (as barras de um ScrollContainer, por exemplo).</summary>
		public static bool IncludeInternal => Bool(InternalKey, false);

		/// <summary>
		/// Grava a origem de todo nó desde a abertura, e não só depois do primeiro Ctrl+F4. Custa uma leitura
		/// de pilha por nó que entra na árvore, mesmo com o overlay desligado.
		/// </summary>
		public static bool CaptureOnStartup => Bool(CaptureKey, false);

		/// <summary>Registra as opções no editor. Valor igual ao padrão não é escrito no project.godot.</summary>
		public static void Register()
		{
			Add(ReleaseKey, false, Variant.Type.Bool, PropertyHint.None, "");
			Add(StartLevelKey, 0, Variant.Type.Int, PropertyHint.Enum, "Off,Boxes,Names,Values,Trace");
			Add(FontSizeKey, DefaultFontSize, Variant.Type.Int, PropertyHint.Range, "6,48");
			Add(InternalKey, false, Variant.Type.Bool, PropertyHint.None, "");
			Add(CaptureKey, false, Variant.Type.Bool, PropertyHint.None, "");
		}

		private static void Add(string name, Variant value, Variant.Type type, PropertyHint hint, string hintText)
		{
			if (!ProjectSettings.HasSetting(name))
				ProjectSettings.SetSetting(name, value);
			ProjectSettings.SetInitialValue(name, value);
			ProjectSettings.AddPropertyInfo(new Dictionary
			{
				["name"] = name,
				["type"] = (int)type,
				["hint"] = (int)hint,
				["hint_string"] = hintText,
			});
		}

		private static bool Bool(string name, bool fallback) =>
			ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).AsBool() : fallback;

		private static int Int(string name, int fallback) =>
			ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).AsInt32() : fallback;
	}
}
