#nullable enable
using Godot;
using Godot.Collections;

namespace VisualDebug
{
	/// <summary>Uma linha de texto pronta, com a largura já medida (medir a cada quadro custaria uma chamada ao motor).</summary>
	internal readonly record struct TextLine(string Text, float Width)
	{
		public static readonly TextLine Empty = new("", 0);

		public bool IsEmpty => Text.Length == 0;
	}

	/// <summary>
	/// A fonte do overlay: monoespaçada do sistema (Consolas, Menlo, DejaVu Sans Mono...), com a fonte
	/// padrão do Godot de reserva, para os números não dançarem quando mudam. Independe do tema do jogo.
	/// </summary>
	internal sealed class TextStyle
	{
		public TextStyle(int size)
		{
			Font = new SystemFont
			{
				FontNames = new[] { "Consolas", "Cascadia Mono", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "monospace" },
				Fallbacks = new Array<Font> { ThemeDB.FallbackFont },
			};
			Size = size;
			Ascent = Font.GetAscent(size);
			LineHeight = Font.GetHeight(size);
		}

		public Font Font { get; }
		public int Size { get; }
		public float Ascent { get; }
		public float LineHeight { get; }

		public TextLine Line(string text) =>
			text.Length == 0 ? TextLine.Empty : new TextLine(text, Font.GetStringSize(text, HorizontalAlignment.Left, -1, Size).X);
	}
}
