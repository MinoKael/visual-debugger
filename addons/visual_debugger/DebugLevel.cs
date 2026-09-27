#nullable enable

namespace VisualDebug
{
	/// <summary>As camadas do modo de depuração. São acumulativas: cada uma mostra tudo o que a anterior mostra.</summary>
	public enum DebugLevel
	{
		/// <summary>Desligado: nada é percorrido nem desenhado.</summary>
		Off = 0,

		/// <summary>Ctrl+F1: o contorno de todo componente.</summary>
		Boxes = 1,

		/// <summary>Ctrl+F2: + o nome do nó e a classe.</summary>
		Names = 2,

		/// <summary>Ctrl+F3: + posição, tamanho, escala e rotação, ao vivo.</summary>
		Values = 3,

		/// <summary>Ctrl+F4: + o script do nó e o ponto do código em que ele entrou na árvore.</summary>
		Trace = 4,
	}
}
