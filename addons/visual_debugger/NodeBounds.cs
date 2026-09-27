#nullable enable
using System;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// O retângulo de um CanvasItem no espaço do próprio nó, antes da transformação global. Control tem
	/// tamanho; dos Node2D, os que desenham algo mensurável (sprite, forma de colisão, polígono, linha,
	/// TileMapLayer, partículas). Os demais (Node2D puro, Marker2D, Camera2D...) viram um ponto na origem.
	/// </summary>
	internal static class NodeBounds
	{
		private static readonly Func<CanvasItem, Rect2?> Polygon = item => item switch
		{
			Polygon2D polygon => Enclose(polygon.Polygon, polygon.Offset, 0),
			CollisionPolygon2D collision => Enclose(collision.Polygon, Vector2.Zero, 0),
			Line2D line => Enclose(line.Points, Vector2.Zero, line.Width / 2),
			_ => null,
		};

		private static readonly Func<CanvasItem, Rect2?> Animated = item =>
		{
			var sprite = (AnimatedSprite2D)item;
			using var animation = sprite.Animation;
			var texture = sprite.SpriteFrames?.GetFrameTexture(animation, sprite.Frame);
			if (texture == null)
				return null;
			var size = texture.GetSize();
			var origin = sprite.Offset - (sprite.Centered ? size / 2 : Vector2.Zero);
			return new Rect2(origin, size);
		};

		public static bool TryGet(CanvasItem item, NodeCard card, int frame, out Rect2 rect)
		{
			switch (item)
			{
				case Control control:
					rect = new Rect2(Vector2.Zero, control.Size);
					return rect.Size != Vector2.Zero;
				case Sprite2D sprite:
					rect = sprite.GetRect();
					return true;
				case CollisionShape2D collision when collision.Shape != null:
					rect = collision.Shape.GetRect();
					return true;
				case TileMapLayer tiles when tiles.TileSet != null:
					var used = tiles.GetUsedRect();
					var tile = (Vector2)tiles.TileSet.TileSize;
					rect = new Rect2((Vector2)used.Position * tile, (Vector2)used.Size * tile);
					return used.Size != Vector2I.Zero;
				case GpuParticles2D particles:
					rect = particles.VisibilityRect;
					return true;
				case AnimatedSprite2D:
					return card.SlowBounds(frame, Animated, out rect);
				case Polygon2D or CollisionPolygon2D or Line2D:
					return card.SlowBounds(frame, Polygon, out rect);
				default:
					rect = default;
					return false;
			}
		}

		private static Rect2? Enclose(Vector2[] points, Vector2 offset, float margin)
		{
			if (points.Length == 0)
				return null;
			var rect = new Rect2(points[0] + offset, Vector2.Zero);
			for (var i = 1; i < points.Length; i++)
				rect = rect.Expand(points[i] + offset);
			return rect.Grow(margin);
		}
	}
}
