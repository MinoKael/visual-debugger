#nullable enable
using System;
using System.Globalization;
using System.Reflection;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// Os valores de transformação de um nó, arredondados na precisão mostrada (0,1 px, 0,01 de escala,
	/// 0,1°): dois retratos iguais significam texto igual, e o texto não é refeito.
	/// </summary>
	internal readonly record struct Snapshot(Vector2 Position, Vector2 Size, Vector2 Scale, float Degrees, bool HasSize)
	{
		public static Snapshot Of(Vector2 position, Vector2 size, Vector2 scale, float degrees, bool hasSize) =>
			new(Round(position, 10), hasSize ? Round(size, 10) : Vector2.Zero, Round(scale, 100), Round(degrees, 10), hasSize);

		public bool Identity => Scale == Vector2.One && Degrees == 0;

		public static Vector2 Round(Vector2 value, float steps) => new(Round(value.X, steps), Round(value.Y, steps));

		// O "== 0" troca o -0 por 0, que senão sairia como "-0" no texto.
		private static float Round(float value, float steps)
		{
			var rounded = MathF.Round(value * steps) / steps;
			return rounded == 0 ? 0 : rounded;
		}
	}

	/// <summary>
	/// O que o overlay guarda de cada nó entre um quadro e outro, para o <c>_Draw</c> não alocar: os textos
	/// prontos, já medidos, só são refeitos quando o valor mostrado muda. Um nó parado custa zero alocação
	/// por quadro; um que se move refaz só a própria linha de valores.
	///
	/// O cartão também segura a referência do nó, e assim o Godot devolve sempre o mesmo objeto C# para ele
	/// em <c>GetChild</c>, em vez de criar um invólucro novo a cada quadro.
	/// </summary>
	internal sealed class NodeCard
	{
		private const int SlowRefreshFrames = 15;

		private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

		private Snapshot _values;
		private bool _hasValues;
		private Rect2 _slowBounds;
		private bool _slowValid;
		private int _slowFrame = -SlowRefreshFrames;

		public NodeCard(CanvasItem item, TextStyle style, int frame)
		{
			Item = item;
			Id = item.GetInstanceId();
			Class = TraceRecorder.TypeName(item.GetType());
			Native = item.GetClass();
			ScriptPath = FindScript(item);
			ScenePath = FindScene(item);
			RefreshName(style, frame);
		}

		public CanvasItem Item { get; }
		public ulong Id { get; }

		/// <summary>A classe C# (a do script, se houver); <see cref="Native"/> é a classe do motor por baixo.</summary>
		public string Class { get; }

		public string Native { get; }
		public string? ScriptPath { get; }
		public string? ScenePath { get; }
		public string Name { get; private set; } = "";
		public Trace? Trace { get; private set; }

		/// <summary>O último quadro em que o nó estava visível; cartão esquecido há tempo é descartado.</summary>
		public int SeenFrame { get; set; }

		public int NameFrame { get; private set; }

		/// <summary>Sobe a cada texto refeito: o inspetor sabe quando se refazer.</summary>
		public int Version { get; private set; }

		public Snapshot Values => _values;

		/// <summary><c>nome : Classe</c>.</summary>
		public TextLine Title { get; private set; } = TextLine.Empty;

		/// <summary><c>pos x, y  size w x h</c>.</summary>
		public TextLine Transform { get; private set; } = TextLine.Empty;

		/// <summary><c>scale x, y  rot d°</c>, só quando foge da identidade.</summary>
		public TextLine Extra { get; private set; } = TextLine.Empty;

		/// <summary><c>Script.cs  @ Arquivo.cs:linha</c>.</summary>
		public TextLine Origin { get; private set; } = TextLine.Empty;

		/// <summary>
		/// Relê o nome. <c>Node.Name</c> cria um <see cref="StringName"/> a cada leitura, então o overlay
		/// chama isto com orçamento (alguns cartões por quadro), e o StringName é descartado na hora.
		/// Nome automático (<c>@Label@123</c>, dado a nó criado em código) encurta para <c>@123</c>.
		/// </summary>
		public void RefreshName(TextStyle style, int frame)
		{
			NameFrame = frame;
			using var name = Item.Name;
			var text = name.ToString();
			if (text == Name && !Title.IsEmpty)
				return;

			Name = text;
			var shown = text.StartsWith('@') ? text[text.LastIndexOf('@')..] : text;
			Title = style.Line(shown == Class ? Class : $"{shown} : {Class}");
			Version++;
		}

		public void Update(in Snapshot values, TextStyle style)
		{
			if (_hasValues && values == _values)
				return;

			_values = values;
			_hasValues = true;
			var position = values.Position;
			var size = values.Size;
			Transform = style.Line(values.HasSize
				? string.Create(Invariant, $"pos {position.X:0.#}, {position.Y:0.#}  size {size.X:0.#} x {size.Y:0.#}")
				: string.Create(Invariant, $"pos {position.X:0.#}, {position.Y:0.#}"));
			Extra = values.Identity
				? TextLine.Empty
				: style.Line(string.Create(Invariant, $"scale {values.Scale.X:0.##}, {values.Scale.Y:0.##}  rot {values.Degrees:0.#}°"));
			Version++;
		}

		public void SetTrace(Trace? trace, TextStyle style)
		{
			if (!Origin.IsEmpty && ReferenceEquals(trace, Trace))
				return;

			Trace = trace;
			var script = ScriptPath == null ? "" : ScriptPath.GetFile() + "  ";
			Origin = style.Line($"{script}@ {trace?.Site ?? "?"}");
			Version++;
		}

		/// <summary>
		/// O retângulo de um nó cuja medida aloca (lê um array de pontos, um StringName): refeito só a cada
		/// <see cref="SlowRefreshFrames"/> quadros.
		/// </summary>
		public bool SlowBounds(int frame, Func<CanvasItem, Rect2?> measure, out Rect2 rect)
		{
			if (frame - _slowFrame >= SlowRefreshFrames)
			{
				_slowFrame = frame;
				var measured = measure(Item);
				_slowValid = measured.HasValue;
				_slowBounds = measured.GetValueOrDefault();
			}

			rect = _slowBounds;
			return _slowValid;
		}

		/// <summary>O arquivo do script: o recurso anexado ou, em C#, o [ScriptPath] que o gerador do Godot põe na classe.</summary>
		private static string? FindScript(CanvasItem item)
		{
			var script = item.GetScript().As<Script>();
			if (script != null && !string.IsNullOrEmpty(script.ResourcePath))
				return script.ResourcePath;
			return item.GetType().GetCustomAttribute<ScriptPathAttribute>()?.Path;
		}

		/// <summary>A cena de onde o nó saiu: a dele, se é a raiz de uma cena instanciada, ou a do dono.</summary>
		private static string? FindScene(CanvasItem item)
		{
			if (!string.IsNullOrEmpty(item.SceneFilePath))
				return item.SceneFilePath;
			var owner = item.Owner;
			return owner != null && !string.IsNullOrEmpty(owner.SceneFilePath) ? owner.SceneFilePath : null;
		}
	}
}
