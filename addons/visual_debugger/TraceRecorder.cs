#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// De onde um nó veio: os quadros do código do projeto na pilha no instante em que ele entrou na
	/// árvore. <see cref="Site"/> é o primeiro deles, curto, para o rótulo (<c>RuneScreen.cs:120</c>);
	/// <see cref="Frames"/> é a cadeia inteira, para o inspetor.
	/// </summary>
	public sealed class Trace
	{
		/// <summary>Sem quadro do projeto na pilha: a cena principal, um autoload ou uma chamada adiada.</summary>
		public static readonly Trace Engine = new("engine", new[] { "(engine: scene load, autoload or deferred call)" });

		public Trace(string site, string[] frames)
		{
			Site = site;
			Frames = frames;
		}

		public string Site { get; }
		public IReadOnlyList<string> Frames { get; }
	}

	/// <summary>
	/// Grava, para cada nó que entra na árvore enquanto está ligado, de que ponto do código ele veio.
	///
	/// A pilha é lida no sinal <c>SceneTree.node_added</c>, que o Godot emite dentro do próprio
	/// <c>AddChild</c>: os quadros de cima são do motor, do .NET e deste addon, e o primeiro quadro do
	/// projeto é quem pendurou o nó (ou instanciou a cena dele). Uma subárvore montada fora da árvore e
	/// pendurada de uma vez aponta inteira para esse <c>AddChild</c>. Nó que entrou antes de ligar fica
	/// sem registro.
	///
	/// Ler a pilha com arquivo e linha é caro (consulta o PDB). Então ela é lida primeiro sem arquivo, só
	/// método e deslocamento de IL, que viram uma chave: cada chave nova paga a leitura completa uma vez,
	/// e as repetidas (uma lista montando cem itens no mesmo laço) reusam o mesmo <see cref="Trace"/>.
	/// </summary>
	public sealed class TraceRecorder
	{
		private const int MaxFrames = 6;

		private static readonly string OwnNamespace = typeof(TraceRecorder).Namespace!;

		private readonly SceneTree _tree;
		private readonly Node _ignored;
		private readonly Dictionary<ulong, Trace> _byNode = new();
		private readonly Dictionary<int, Trace> _bySite = new();
		private readonly List<ulong> _dead = new();
		private bool _capturing;

		/// <param name="ignored">A raiz do próprio addon, que não é gravada.</param>
		public TraceRecorder(SceneTree tree, Node ignored)
		{
			_tree = tree;
			_ignored = ignored;
		}

		public bool Capturing
		{
			get => _capturing;
			set
			{
				if (value == _capturing)
					return;
				_capturing = value;
				if (value)
					_tree.NodeAdded += OnNodeAdded;
				else
					_tree.NodeAdded -= OnNodeAdded;
			}
		}

		/// <summary>A origem gravada do nó, ou null se ele entrou na árvore com a gravação desligada.</summary>
		public Trace? For(ulong instanceId) => _byNode.TryGetValue(instanceId, out var trace) ? trace : null;

		/// <summary>Esquece os nós já liberados. Sem alocação: a lista de mortos é reaproveitada.</summary>
		public void Purge()
		{
			_dead.Clear();
			foreach (var id in _byNode.Keys)
			{
				if (!GodotObject.IsInstanceIdValid(id))
					_dead.Add(id);
			}

			foreach (var id in _dead)
				_byNode.Remove(id);
		}

		private void OnNodeAdded(Node node)
		{
			if (node == _ignored || _ignored.IsAncestorOf(node))
				return;
			_byNode[node.GetInstanceId()] = Capture();
		}

		private Trace Capture()
		{
			var stack = new StackTrace(1, false);
			var key = new HashCode();
			var found = 0;
			for (var i = 0; i < stack.FrameCount && found < MaxFrames; i++)
			{
				var frame = stack.GetFrame(i);
				var method = frame?.GetMethod();
				if (method == null || !IsProject(method))
					continue;
				key.Add(method);
				key.Add(frame!.GetILOffset());
				found++;
			}

			if (found == 0)
				return Trace.Engine;

			var site = key.ToHashCode();
			if (!_bySite.TryGetValue(site, out var trace))
			{
				trace = Resolve(new StackTrace(1, true));
				_bySite[site] = trace;
			}

			return trace;
		}

		private static Trace Resolve(StackTrace stack)
		{
			var frames = new List<string>(MaxFrames);
			string? site = null;
			for (var i = 0; i < stack.FrameCount && frames.Count < MaxFrames; i++)
			{
				var frame = stack.GetFrame(i);
				var method = frame?.GetMethod();
				if (method == null || !IsProject(method))
					continue;

				var name = Describe(method);
				var file = frame!.GetFileName();
				var line = frame.GetFileLineNumber();
				frames.Add(file == null ? name : $"{name}  {ProjectSettings.LocalizePath(file)}:{line}");
				site ??= file == null ? name : $"{Path.GetFileName(file)}:{line}";
			}

			return site == null ? Trace.Engine : new Trace(site, frames.ToArray());
		}

		/// <summary>
		/// Quadro do projeto: fora do Godot, do .NET e deste addon. Os métodos que o gerador de código do
		/// Godot põe na classe do projeto (<c>InvokeGodotClassMethod</c> e parentes) também ficam de fora.
		/// </summary>
		private static bool IsProject(MethodBase method)
		{
			var type = method.DeclaringType;
			if (type == null || method.Name.Contains("GodotClass", StringComparison.Ordinal))
				return false;

			var space = type.Namespace;
			if (space == null)
				return true;
			return !(space.StartsWith("Godot", StringComparison.Ordinal)
				|| space.StartsWith("System", StringComparison.Ordinal)
				|| space.StartsWith("Microsoft", StringComparison.Ordinal)
				|| space == OwnNamespace
				|| space.StartsWith(OwnNamespace + ".", StringComparison.Ordinal));
		}

		/// <summary>
		/// <c>Tipo.Método</c> legível. Os nomes que o compilador gera voltam ao método que os escreveu:
		/// lambda (<c>&lt;Build&gt;b__3_0</c> → <c>Build (lambda)</c>), função local
		/// (<c>&lt;Build&gt;g__Row|3_0</c> → <c>Build.Row</c>) e máquina de estado de async/iterador
		/// (<c>&lt;Load&gt;d__5.MoveNext</c> → <c>Load</c>).
		/// </summary>
		private static string Describe(MethodBase method)
		{
			var type = method.DeclaringType!;
			var name = method.Name;
			if (name == "MoveNext" && type.Name.StartsWith('<'))
				name = type.Name;
			while (type.IsNested && type.Name.StartsWith('<'))
				type = type.DeclaringType!;

			var close = name.IndexOf('>');
			if (name.StartsWith('<') && close > 1)
			{
				var rest = name[(close + 1)..];
				var bar = rest.IndexOf('|');
				name = rest.StartsWith("b__", StringComparison.Ordinal) ? name[1..close] + " (lambda)"
					: rest.StartsWith("g__", StringComparison.Ordinal) && bar > 3 ? name[1..close] + "." + rest[3..bar]
					: name[1..close];
			}

			var owner = TypeName(type);
			return name == ".ctor" ? $"new {owner}()" : $"{owner}.{name}";
		}

		/// <summary>O nome do tipo sem a aridade dos genéricos (<c>List`1</c> → <c>List</c>).</summary>
		internal static string TypeName(Type type)
		{
			var name = type.Name;
			var tick = name.IndexOf('`');
			return tick > 0 ? name[..tick] : name;
		}
	}
}
