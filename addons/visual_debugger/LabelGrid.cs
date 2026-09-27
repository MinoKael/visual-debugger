#nullable enable
using System;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// Onde já há rótulo na tela, numa grade grossa de células de 16 px: um rótulo novo só entra se todas
	/// as células que ele cobre estão livres. Evita a sopa de letras sem comparar rótulo com rótulo (que
	/// seria quadrático), e a grade é reaproveitada entre quadros.
	/// </summary>
	internal sealed class LabelGrid
	{
		private const int Cell = 16;

		private bool[] _cells = Array.Empty<bool>();
		private int _columns;
		private int _rows;

		public void Reset(Vector2 viewport)
		{
			_columns = (int)Math.Ceiling(viewport.X / Cell) + 1;
			_rows = (int)Math.Ceiling(viewport.Y / Cell) + 1;
			var length = _columns * _rows;
			if (length > _cells.Length)
				_cells = new bool[length];
			else
				Array.Clear(_cells, 0, length);
		}

		/// <summary>Ocupa as células do retângulo se todas estão livres.</summary>
		public bool TryTake(Rect2 rect)
		{
			Span(rect, out var x0, out var y0, out var x1, out var y1);
			for (var y = y0; y <= y1; y++)
			{
				for (var x = x0; x <= x1; x++)
				{
					if (_cells[y * _columns + x])
						return false;
				}
			}

			Mark(x0, y0, x1, y1);
			return true;
		}

		/// <summary>Ocupa as células mesmo que já tenham dono (o rótulo do nó sob o mouse, o painel do topo).</summary>
		public void Take(Rect2 rect)
		{
			Span(rect, out var x0, out var y0, out var x1, out var y1);
			Mark(x0, y0, x1, y1);
		}

		private void Mark(int x0, int y0, int x1, int y1)
		{
			for (var y = y0; y <= y1; y++)
			{
				for (var x = x0; x <= x1; x++)
					_cells[y * _columns + x] = true;
			}
		}

		private void Span(Rect2 rect, out int x0, out int y0, out int x1, out int y1)
		{
			x0 = Math.Clamp((int)(rect.Position.X / Cell), 0, _columns - 1);
			y0 = Math.Clamp((int)(rect.Position.Y / Cell), 0, _rows - 1);
			x1 = Math.Clamp((int)((rect.End.X - 1) / Cell), 0, _columns - 1);
			y1 = Math.Clamp((int)((rect.End.Y - 1) / Cell), 0, _rows - 1);
		}
	}
}
