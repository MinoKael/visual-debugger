#nullable enable
using System;
using Godot;

namespace VisualDebug
{
	/// <summary>
	/// O modo de depuração visual, inspirado no Debug CSS dos navegadores. É o autoload <c>VisualDebug</c>:
	/// põe um CanvasLayer acima de tudo (camada 128) com o <see cref="DebugOverlay"/>, que desenha os
	/// contornos, nomes, valores e origem dos nós Control e Node2D por cima do jogo rodando.
	///
	/// As camadas são acumulativas, uma tecla cada: Ctrl+F1 contornos, Ctrl+F2 + nomes, Ctrl+F3 + valores
	/// ao vivo, Ctrl+F4 + script e origem no código. Apertar a da camada atual desliga. As teclas são as
	/// ações <c>visual_debug_boxes</c>, <c>_names</c>, <c>_values</c> e <c>_trace</c>: se o projeto já as
	/// define no Mapa de Entrada, valem as dele. Funciona com o jogo pausado.
	///
	/// Em build de release se remove sozinho, a menos que <see cref="DebugSettings"/> peça o contrário.
	/// </summary>
	public partial class VisualDebugger : Node
	{
		/// <summary>A camada do CanvasLayer: a mais alta que o Godot aceita.</summary>
		public const int CanvasLayerIndex = 128;

		private const int PurgeTracesEvery = 600;

		private static readonly StringName[] Actions =
		{
			"visual_debug_boxes",
			"visual_debug_names",
			"visual_debug_values",
			"visual_debug_trace",
		};

		private static readonly Key[] Keys = { Key.F1, Key.F2, Key.F3, Key.F4 };

		private CanvasLayer? _canvas;
		private DebugOverlay? _overlay;
		private TraceRecorder? _traces;
		private DebugLevel _level;
		private int _tick;

		/// <summary>O autoload em execução (null em release ou antes de entrar na árvore).</summary>
		public static VisualDebugger? Instance { get; private set; }

		/// <summary>Avisa a cada troca de camada.</summary>
		public event Action<DebugLevel>? LevelChanged;

		/// <summary>A camada atual; pode ser trocada por código, além das teclas.</summary>
		public DebugLevel Level
		{
			get => _level;
			set => Apply(value);
		}

		/// <summary>
		/// Grava a origem de cada nó que entra na árvore. Liga sozinho no Ctrl+F4 e desliga com o modo (salvo
		/// com <c>capture_on_startup</c>); ligar à mão antes de abrir uma tela grava a montagem dela inteira.
		/// </summary>
		public bool CaptureTraces
		{
			get => _traces?.Capturing ?? false;
			set
			{
				if (_traces != null)
					_traces.Capturing = value;
			}
		}

		public override void _EnterTree()
		{
			if (!DebugSettings.Enabled)
				return;

			Instance = this;
			ProcessMode = ProcessModeEnum.Always;
			// A gravação liga aqui, antes do _Ready, para pegar a cena principal inteira.
			_traces = new TraceRecorder(GetTree(), this) { Capturing = DebugSettings.CaptureOnStartup };
		}

		public override void _Ready()
		{
			if (_traces == null)
			{
				QueueFree();
				return;
			}

			RegisterActions();
			_canvas = new CanvasLayer { Name = "Canvas", Layer = CanvasLayerIndex };
			_overlay = new DebugOverlay(this, _traces, new TextStyle(DebugSettings.FontSize), DebugSettings.IncludeInternal) { Name = "Overlay" };
			_canvas.AddChild(_overlay);
			AddChild(_canvas);
			Apply(DebugSettings.StartLevel);
		}

		public override void _ExitTree()
		{
			if (_traces != null)
				_traces.Capturing = false;
			if (Instance == this)
				Instance = null;
		}

		public override void _Process(double delta)
		{
			if (_level != DebugLevel.Off)
				_overlay!.QueueRedraw();
			if (++_tick % PurgeTracesEvery == 0)
				_traces!.Purge();
		}

		public override void _Input(InputEvent @event)
		{
			if (@event is not InputEventKey { Pressed: true, Echo: false })
				return;

			for (var i = 0; i < Actions.Length; i++)
			{
				if (!@event.IsActionPressed(Actions[i], false, true))
					continue;

				var chosen = (DebugLevel)(i + 1);
				Apply(_level == chosen ? DebugLevel.Off : chosen);
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		private void Apply(DebugLevel level)
		{
			if (_overlay == null || _canvas == null || _traces == null)
				return;

			_level = level;
			_overlay.Level = level;
			_canvas.Visible = level != DebugLevel.Off;
			if (level == DebugLevel.Trace)
				_traces.Capturing = true;
			else if (level == DebugLevel.Off && !DebugSettings.CaptureOnStartup)
				_traces.Capturing = false;
			LevelChanged?.Invoke(level);
		}

		/// <summary>Cria as ações que o projeto não definiu, com Ctrl+F1 a Ctrl+F4.</summary>
		private static void RegisterActions()
		{
			for (var i = 0; i < Actions.Length; i++)
			{
				if (InputMap.HasAction(Actions[i]))
					continue;
				InputMap.AddAction(Actions[i]);
				InputMap.ActionAddEvent(Actions[i], new InputEventKey { Keycode = Keys[i], CtrlPressed = true });
			}
		}
	}
}
