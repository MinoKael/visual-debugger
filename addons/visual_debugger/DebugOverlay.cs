#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// O desenho do modo de depuração: um Node2D no CanvasLayer mais alto que, a cada quadro, percorre a
	/// árvore e desenha no próprio <c>_Draw</c>, por cima do jogo, em quatro passos:
	///
	/// 1. Coleta: desce a árvore com <c>GetChildCount</c>/<c>GetChild</c> (sem o array que
	///    <c>GetChildren</c> criaria), pula o invisível, o que é de outra Viewport (janelas, SubViewport) e
	///    o que está fora da tela, e guarda cada item num array reaproveitado.
	/// 2. Contornos, na cor da profundidade, como o Debug CSS: Control em linha cheia, Node2D tracejado,
	///    Node2D sem tamanho como uma cruz na origem. Rotação e escala entram (o contorno gira junto).
	/// 3. Rótulos, na ordem da árvore (o componente antes das peças de dentro dele): cada um entra acima
	///    do canto do componente ou, sem espaço, dentro dele; se os dois lugares já têm rótulo
	///    (<see cref="LabelGrid"/>), fica de fora. O nó sob o mouse sempre ganha o seu.
	/// 4. O inspetor do nó sob o mouse: tudo o que a camada permite, sem disputar espaço.
	///
	/// Alocação: os textos moram nos <see cref="NodeCard"/> e só são refeitos quando o valor mostrado muda,
	/// então uma tela parada desenha sem alocar nada; um componente em movimento refaz só a sua linha.
	/// </summary>
	public partial class DebugOverlay : Node2D
	{
		private const float Pad = 3;
		private const int NameChecksPerFrame = 8;
		private const int NameRefreshFrames = 60;
		private const int PurgeEvery = 60;

		/// <summary>A cor de cada profundidade, repetindo a cada oito.</summary>
		private static readonly Color[] Hues =
		{
			new(1.00f, 0.36f, 0.36f),
			new(1.00f, 0.62f, 0.22f),
			new(0.98f, 0.88f, 0.25f),
			new(0.46f, 0.88f, 0.36f),
			new(0.28f, 0.84f, 0.88f),
			new(0.42f, 0.58f, 1.00f),
			new(0.72f, 0.46f, 1.00f),
			new(1.00f, 0.46f, 0.80f),
		};

		private static readonly Color Backing = new(0.05f, 0.05f, 0.07f, 0.85f);
		private static readonly Color ValueInk = new(0.92f, 0.92f, 0.92f);
		private static readonly Color ExtraInk = new(0.86f, 0.86f, 0.62f);
		private static readonly Color TraceInk = new(1.00f, 0.80f, 0.45f);
		private static readonly Color DimInk = new(0.62f, 0.63f, 0.70f);
		private static readonly Color MouseInk = new(0.55f, 0.86f, 1.00f);
		private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
		private static readonly string[] LevelNames = { "Off", "Boxes", "Names", "Values", "Trace" };

		private readonly Node _ignored;
		private readonly TraceRecorder _traces;
		private readonly TextStyle _text;
		private readonly bool _includeInternal;
		private readonly Dictionary<CanvasItem, NodeCard> _cards = new(ReferenceEqualityComparer.Instance);
		private readonly List<CanvasItem> _stale = new();
		private readonly LabelGrid _grid = new();
		private readonly TextLine[] _lines = new TextLine[4];
		private readonly Color[] _inks = new Color[4];
		private readonly Vector2[] _quad = new Vector2[4];
		private readonly List<(TextLine Line, Color Ink)> _inspector = new();
		private Entry[] _entries = new Entry[512];
		private DebugLevel _level;
		private int _count;
		private int _visited;
		private int _frame;
		private int _nameBudget;
		private double _drawMs;
		private long _drawBytes;
		private TextLine _hud = TextLine.Empty;
		private readonly TextLine _mouseLabel;
		private readonly TextLine _comma;
		private TextLine[] _coordinates = new TextLine[2048];
		private NodeCard? _inspected;
		private int _inspectedVersion;
		private DebugLevel _inspectedLevel;
		private Vector2 _inspectedGlobal;
		private string _inspectedPath = "";

		/// <param name="ignored">A raiz do addon, que não é desenhada.</param>
		/// <param name="includeInternal">Desce também nos filhos internos dos controles.</param>
		internal DebugOverlay(Node ignored, TraceRecorder traces, TextStyle text, bool includeInternal)
		{
			_ignored = ignored;
			_traces = traces;
			_text = text;
			_mouseLabel = text.Line(" · mouse ");
			_comma = text.Line(",");
			_includeInternal = includeInternal;
		}

		public DebugLevel Level
		{
			get => _level;
			set
			{
				_level = value;
				_hud = TextLine.Empty;
				if (value == DebugLevel.Off)
				{
					// Solta as referências: nada fica preso a nó já liberado enquanto o modo está desligado.
					_cards.Clear();
					Array.Clear(_entries, 0, _entries.Length);
					_count = 0;
					_inspected = null;
				}

				QueueRedraw();
			}
		}

		public override void _Draw()
		{
			if (_level == DebugLevel.Off)
				return;

			var started = Stopwatch.GetTimestamp();
			var allocated = GC.GetAllocatedBytesForCurrentThread();
			_frame++;
			_count = 0;
			_visited = 0;
			_nameBudget = NameChecksPerFrame;
			var view = GetViewportRect();
			Collect(GetTree().Root, 0, 0, view);

			var hovered = FindHovered(GetLocalMousePosition());
			DrawBoxes(hovered);
			_grid.Reset(view.End);
			DrawHud(view);
			if (_level >= DebugLevel.Names)
			{
				if (hovered >= 0)
					DrawLabel(_entries[hovered], view, true);
				for (var i = 0; i < _count; i++)
				{
					if (i != hovered)
						DrawLabel(_entries[i], view, false);
				}

				if (hovered >= 0)
					DrawInspector(_entries[hovered], view);
			}

			if (_frame % PurgeEvery == 0)
				PurgeCards();
			_drawMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
			_drawBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
		}

		private void Collect(Node node, int depth, int layer, Rect2 view)
		{
			var count = node.GetChildCount(_includeInternal);
			for (var i = 0; i < count; i++)
			{
				var child = node.GetChild(i, _includeInternal);
				if (child == _ignored || child is Viewport)
					continue;

				switch (child)
				{
					case CanvasLayer canvas:
						if (canvas.Visible)
							Collect(canvas, depth, canvas.Layer, view);
						break;
					case CanvasItem item:
						if (!item.Visible)
							break;
						Visit(item, depth, layer, view);
						Collect(item, depth + 1, layer, view);
						break;
					default:
						Collect(child, depth, layer, view);
						break;
				}
			}
		}

		private void Visit(CanvasItem item, int depth, int layer, Rect2 view)
		{
			_visited++;
			if (!_cards.TryGetValue(item, out var card))
			{
				card = new NodeCard(item, _text, _frame);
				_cards[item] = card;
			}
			else if (_level >= DebugLevel.Names && _nameBudget > 0 && _frame - card.NameFrame > NameRefreshFrames)
			{
				_nameBudget--;
				card.RefreshName(_text, _frame);
			}

			card.SeenFrame = _frame;
			var hasRect = NodeBounds.TryGet(item, card, _frame, out var local);
			var transform = item.GetGlobalTransformWithCanvas();
			var screen = hasRect ? Enclose(transform, local) : new Rect2(transform.Origin, Vector2.Zero);
			if (!screen.Grow(8).Intersects(view))
				return;

			if (_level >= DebugLevel.Values)
				card.Update(Read(item, local, hasRect), _text);
			if (_level >= DebugLevel.Trace)
				card.SetTrace(_traces.For(card.Id), _text);

			if (_count == _entries.Length)
				Array.Resize(ref _entries, _count * 2);
			_entries[_count++] = new Entry(card, transform, local, screen, hasRect, item is Control, depth, layer);
		}

		private static Snapshot Read(CanvasItem item, Rect2 local, bool hasRect) => item switch
		{
			Control control => Snapshot.Of(control.Position, control.Size, control.Scale, control.RotationDegrees, true),
			Node2D node => Snapshot.Of(node.Position, local.Size, node.Scale, node.RotationDegrees, hasRect),
			_ => default,
		};

		/// <summary>O item mais de cima sob o mouse: o de CanvasLayer mais alto e, empatado, o último desenhado.</summary>
		private int FindHovered(Vector2 mouse)
		{
			var best = -1;
			for (var i = 0; i < _count; i++)
			{
				if (Contains(_entries[i], mouse) && (best < 0 || _entries[i].Layer >= _entries[best].Layer))
					best = i;
			}

			return best;
		}

		private static bool Contains(in Entry entry, Vector2 point)
		{
			if (!entry.HasRect)
				return entry.Transform.Origin.DistanceSquaredTo(point) <= 36;
			if (!entry.Screen.HasPoint(point))
				return false;
			if (entry.Aligned)
				return true;
			if (Mathf.Abs(entry.Transform.Determinant()) < 1e-6f)
				return false;
			return entry.Local.HasPoint(entry.Transform.AffineInverse() * point);
		}

		private void DrawBoxes(int hovered)
		{
			for (var i = 0; i < _count; i++)
			{
				ref readonly var entry = ref _entries[i];
				var hue = Hue(entry.Depth);
				var focus = i == hovered;
				if (!entry.HasRect)
				{
					var origin = entry.Transform.Origin;
					DrawLine(origin - new Vector2(5, 0), origin + new Vector2(5, 0), hue, focus ? 2 : -1);
					DrawLine(origin - new Vector2(0, 5), origin + new Vector2(0, 5), hue, focus ? 2 : -1);
					continue;
				}

				var width = focus ? 2 : -1;
				if (entry.IsControl && entry.Aligned)
				{
					if (focus)
						DrawRect(entry.Screen, new Color(hue, 0.16f));
					DrawRect(entry.Screen, hue, false, width);
					continue;
				}

				Corners(entry);
				if (focus)
					DrawColoredPolygon(_quad, new Color(hue, 0.16f));
				for (var k = 0; k < 4; k++)
				{
					var from = _quad[k];
					var to = _quad[(k + 1) % 4];
					if (entry.IsControl)
						DrawLine(from, to, hue, width);
					else
						DrawDashedLine(from, to, hue, width, 4);
				}
			}
		}

		/// <summary>
		/// A barra do topo: camada, quantos nós visíveis, quanto tempo e memória gerenciada o overlay gastou no
		/// último quadro e, na ponta direita, a posição do mouse na tela. O texto da esquerda é refeito a cada
		/// 20 quadros, e o quadro mostrado é sempre o anterior ao da troca, então a própria barra não entra na
		/// conta. O mouse muda todo quadro sem alocar: cada coordenada vira texto uma vez só (<see cref="Coordinate"/>).
		/// </summary>
		private void DrawHud(Rect2 view)
		{
			if (_hud.IsEmpty || _frame % 20 == 0)
			{
				var tracing = _traces.Capturing ? " · tracing" : "";
				_hud = _text.Line(string.Create(Invariant,
					$"VISUAL DEBUG · Ctrl+F{(int)_level} {LevelNames[(int)_level]} · {_visited} nodes · {_drawMs:0.00} ms · {_drawBytes} B{tracing}"));
			}

			var mouse = GetLocalMousePosition();
			var x = Coordinate(Mathf.RoundToInt(mouse.X));
			var y = Coordinate(Mathf.RoundToInt(mouse.Y));
			var width = _hud.Width + _mouseLabel.Width + x.Width + _comma.Width + y.Width;
			var size = new Vector2(width + Pad * 2 + 2, _text.LineHeight + Pad);
			var rect = new Rect2(new Vector2(view.End.X - size.X - 6, view.Position.Y + 6), size);
			_grid.Take(rect);
			DrawRect(rect, Backing);
			DrawRect(new Rect2(rect.Position, new Vector2(2, rect.Size.Y)), Hue(0));
			var at = rect.Position + new Vector2(Pad + 2, Pad * 0.5f + _text.Ascent);
			at.X = DrawText(_hud, at, ValueInk);
			at.X = DrawText(_mouseLabel, at, DimInk);
			at.X = DrawText(x, at, MouseInk);
			at.X = DrawText(_comma, at, DimInk);
			DrawText(y, at, MouseInk);
		}

		/// <summary>Escreve a linha e devolve onde ela termina.</summary>
		private float DrawText(TextLine line, Vector2 at, Color ink)
		{
			DrawString(_text.Font, at, line.Text, HorizontalAlignment.Left, -1, _text.Size, ink);
			return at.X + line.Width;
		}

		/// <summary>
		/// Uma coordenada do mouse, alinhada à direita em 5 casas (a barra não treme quando o número muda de
		/// tamanho). Feita uma vez por valor e guardada; fora de 0 a 9999 (mouse fora da janela) é feita na hora.
		/// </summary>
		private TextLine Coordinate(int value)
		{
			if (value is < 0 or > 9999)
				return _text.Line(value.ToString(Invariant).PadLeft(5));
			if (value >= _coordinates.Length)
				Array.Resize(ref _coordinates, Math.Max(value + 1, _coordinates.Length * 2));
			if (_coordinates[value].Text == null)
				_coordinates[value] = _text.Line(value.ToString(Invariant).PadLeft(5));
			return _coordinates[value];
		}

		private void DrawLabel(in Entry entry, Rect2 view, bool forced)
		{
			var card = entry.Card;
			var lines = 0;
			Push(ref lines, card.Title, Hue(entry.Depth).Lightened(0.35f));
			if (_level >= DebugLevel.Values)
			{
				Push(ref lines, card.Transform, ValueInk);
				Push(ref lines, card.Extra, ExtraInk);
			}

			if (_level >= DebugLevel.Trace)
				Push(ref lines, card.Origin, TraceInk);

			var width = 0f;
			for (var i = 0; i < lines; i++)
				width = Mathf.Max(width, _lines[i].Width);

			var size = new Vector2(width + Pad * 2 + 2, lines * _text.LineHeight + Pad);
			var anchor = entry.Screen.Position;
			var above = Fit(new Rect2(new Vector2(anchor.X, anchor.Y - size.Y), size), view);
			var inside = Fit(new Rect2(anchor, size), view);
			Rect2 rect;
			if (forced)
			{
				rect = above;
				_grid.Take(rect);
			}
			else if (_grid.TryTake(above))
				rect = above;
			else if (_grid.TryTake(inside))
				rect = inside;
			else
				return;

			DrawPanel(rect, Hue(entry.Depth), lines);
		}

		private void Push(ref int lines, TextLine line, Color ink)
		{
			if (line.IsEmpty)
				return;
			_lines[lines] = line;
			_inks[lines] = ink;
			lines++;
		}

		/// <summary>Fundo escuro, filete na cor da profundidade e as linhas de <see cref="_lines"/>.</summary>
		private void DrawPanel(Rect2 rect, Color accent, int lines)
		{
			DrawRect(rect, Backing);
			DrawRect(new Rect2(rect.Position, new Vector2(2, rect.Size.Y)), accent);
			var baseline = rect.Position + new Vector2(Pad + 2, Pad * 0.5f + _text.Ascent);
			for (var i = 0; i < lines; i++)
			{
				DrawString(_text.Font, baseline, _lines[i].Text, HorizontalAlignment.Left, -1, _text.Size, _inks[i]);
				baseline.Y += _text.LineHeight;
			}
		}

		private void DrawInspector(in Entry entry, Rect2 view)
		{
			var card = entry.Card;
			var global = Snapshot.Round(card.Item is Control control ? control.GlobalPosition : ((Node2D)card.Item).GlobalPosition, 10);
			if (card != _inspected || card.Version != _inspectedVersion || _level != _inspectedLevel || global != _inspectedGlobal)
				BuildInspector(entry, global);

			var width = 0f;
			foreach (var (line, _) in _inspector)
				width = Mathf.Max(width, line.Width);

			var size = new Vector2(width + Pad * 4, _inspector.Count * _text.LineHeight + Pad * 3);
			// Acima e à direita do mouse: embaixo dele fica a dica (tooltip) do jogo, que é uma janela e
			// sempre desenha por cima de qualquer CanvasLayer.
			var mouse = GetLocalMousePosition();
			var position = new Vector2(mouse.X + 16, mouse.Y - size.Y - 16);
			if (position.X + size.X > view.End.X)
				position.X = mouse.X - size.X - 16;
			if (position.Y < view.Position.Y)
				position.Y = mouse.Y + 28;

			var rect = Fit(new Rect2(position, size), view);
			DrawRect(rect, new Color(Backing, 0.94f));
			DrawRect(rect, Hue(entry.Depth), false, 1);
			var baseline = rect.Position + new Vector2(Pad * 2, Pad * 1.5f + _text.Ascent);
			foreach (var (line, ink) in _inspector)
			{
				DrawString(_text.Font, baseline, line.Text, HorizontalAlignment.Left, -1, _text.Size, ink);
				baseline.Y += _text.LineHeight;
			}
		}

		/// <summary>Refeito só quando muda o nó, a camada ou algum valor: fora isso o inspetor não aloca.</summary>
		private void BuildInspector(in Entry entry, Vector2 global)
		{
			var card = entry.Card;
			var item = card.Item;
			if (card != _inspected)
			{
				using var path = item.GetPath();
				_inspectedPath = Elide(path.ToString());
			}

			_inspected = card;
			_inspectedVersion = card.Version;
			_inspectedLevel = _level;
			_inspectedGlobal = global;
			_inspector.Clear();

			Add(card.Title.Text, Hue(entry.Depth).Lightened(0.35f));
			Add(card.Class == card.Native ? card.Native : $"{card.Class} extends {card.Native}", DimInk);
			Add(_inspectedPath, DimInk);

			if (_level >= DebugLevel.Values)
			{
				var values = card.Values;
				Add(string.Create(Invariant, $"pos     {values.Position.X:0.#}, {values.Position.Y:0.#}"), ValueInk);
				Add(string.Create(Invariant, $"global  {global.X:0.#}, {global.Y:0.#}"), ValueInk);
				if (values.HasSize)
					Add(string.Create(Invariant, $"size    {values.Size.X:0.#} x {values.Size.Y:0.#}"), ValueInk);
				Add(string.Create(Invariant, $"scale   {values.Scale.X:0.##}, {values.Scale.Y:0.##}"), ValueInk);
				Add(string.Create(Invariant, $"rot     {values.Degrees:0.#}°"), ValueInk);
				if (item is Control control)
				{
					var pivot = control.PivotOffset;
					Add(string.Create(Invariant, $"pivot   {pivot.X:0.#}, {pivot.Y:0.#}"), ValueInk);
					Add($"mouse   {control.MouseFilter}", ValueInk);
				}
			}

			if (_level >= DebugLevel.Trace)
			{
				Add($"script  {card.ScriptPath ?? "(none)"}", TraceInk);
				if (card.ScenePath != null)
					Add($"scene   {card.ScenePath}", TraceInk);
				Add("added at", TraceInk);
				if (card.Trace == null)
					Add("  (entered the tree before tracing began)", DimInk);
				else
				{
					foreach (var frame in card.Trace.Frames)
						Add("  " + frame, ValueInk);
				}
			}
		}

		private void Add(string text, Color ink) => _inspector.Add((_text.Line(text), ink));

		/// <summary>Caminho longo (comum em interface montada em código) fica só com o fim: <c>…/Pai/Nó</c>.</summary>
		private static string Elide(string path)
		{
			const int Max = 72;
			if (path.Length <= Max)
				return path;
			var cut = path.IndexOf('/', path.Length - Max);
			return cut < 0 ? path : "…" + path[cut..];
		}

		private void PurgeCards()
		{
			_stale.Clear();
			foreach (var (item, card) in _cards)
			{
				if (_frame - card.SeenFrame > PurgeEvery)
					_stale.Add(item);
			}

			foreach (var item in _stale)
				_cards.Remove(item);
			if (_inspected != null && _frame - _inspected.SeenFrame > PurgeEvery)
				_inspected = null;
		}

		private void Corners(in Entry entry)
		{
			var local = entry.Local;
			_quad[0] = entry.Transform * local.Position;
			_quad[1] = entry.Transform * new Vector2(local.End.X, local.Position.Y);
			_quad[2] = entry.Transform * local.End;
			_quad[3] = entry.Transform * new Vector2(local.Position.X, local.End.Y);
		}

		private static Rect2 Enclose(Transform2D transform, Rect2 local)
		{
			var a = transform * local.Position;
			var b = transform * new Vector2(local.End.X, local.Position.Y);
			var c = transform * local.End;
			var d = transform * new Vector2(local.Position.X, local.End.Y);
			var min = new Vector2(Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X)), Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y)));
			var max = new Vector2(Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X)), Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y)));
			return new Rect2(min, max - min);
		}

		/// <summary>Empurra o retângulo para dentro da tela.</summary>
		private static Rect2 Fit(Rect2 rect, Rect2 view)
		{
			var x = Mathf.Clamp(rect.Position.X, view.Position.X, Mathf.Max(view.Position.X, view.End.X - rect.Size.X));
			var y = Mathf.Clamp(rect.Position.Y, view.Position.Y, Mathf.Max(view.Position.Y, view.End.Y - rect.Size.Y));
			return new Rect2(new Vector2(x, y), rect.Size);
		}

		private static Color Hue(int depth) => Hues[depth % Hues.Length];

		/// <summary>Um item visível neste quadro, com a transformação já lida (lida uma vez, usada três).</summary>
		private readonly record struct Entry(NodeCard Card, Transform2D Transform, Rect2 Local, Rect2 Screen, bool HasRect, bool IsControl, int Depth, int Layer)
		{
			/// <summary>Sem rotação nem inclinação: o contorno é o próprio retângulo da tela.</summary>
			public bool Aligned => Transform.X.Y == 0 && Transform.Y.X == 0;
		}
	}
}
