# Visual Debug Mode (Godot 4 .NET)

Overlay de depuração em tempo de execução, inspirado na extensão Debug CSS dos navegadores. Com o
jogo rodando, desenha por cima de tudo o contorno de cada nó `Control` e `Node2D` e, conforme a
camada, o nome, os valores de transformação ao vivo e de onde o nó veio no código.

## Teclas

As camadas são acumulativas: cada uma mostra tudo o que a anterior mostra. Apertar a tecla da camada
ligada desliga o overlay.

| Tecla | Camada | Mostra |
| --- | --- | --- |
| Ctrl+F1 | Boxes | O contorno de todo componente, na cor da profundidade: linha cheia em `Control`, tracejada em `Node2D`, uma cruz no `Node2D` sem tamanho. O contorno gira e escala junto com o nó. |
| Ctrl+F2 | Names | + um rótulo com `nome : Classe`. Nó sem nome, criado em código (`@Label@123`), aparece como `@123`. |
| Ctrl+F3 | Values | + posição e tamanho, e também escala e rotação quando fogem do padrão. Os valores acompanham o nó a cada quadro. |
| Ctrl+F4 | Trace | + o script do nó e o arquivo e a linha onde ele entrou na árvore (`RuneTile.cs  @ RuneScreen.cs:120`). |

Com o mouse sobre um nó, ele se destaca e ganha um inspetor com tudo o que a camada permite:

- a classe C# e a do motor, e o caminho na árvore;
- a posição local e a global, o tamanho, a escala, a rotação, o pivô e o `MouseFilter`;
- o caminho do script e da cena, e a pilha de chamadas de onde o nó entrou na árvore.

A barra do topo mostra a camada, quantos nós estão visíveis, quanto tempo e memória (bytes alocados)
o overlay gastou no último quadro e, na ponta direita, a posição x, y do mouse na tela.

## Instalação num projeto novo

Precisa do Godot 4.3 ou mais novo, na versão .NET.

1. **Projeto C#.** Se o projeto ainda não tem um `.csproj`, vá em *Projeto → Ferramentas → C# →
   Criar solução C#*.
2. **Copiar a pasta.** Copie a pasta `addons/visual_debug` para dentro do projeto. O lugar
   recomendado é `res://addons/visual_debug`, mas qualquer pasta serve: o plugin acha o próprio caminho.
3. **Compilar.** Use o botão *Build* (o martelo, no alto à direita do editor) ou rode `dotnet build`.
   O editor só enxerga plugin em C# depois de compilado.
4. **Ativar.** Em *Projeto → Configurações do Projeto → Plugins*, marque **Visual Debug Mode**. O plugin
   cria o autoload `VisualDebug`.
   - Sem o plugin, o autoload pode ser adicionado à mão: *Configurações do Projeto → Globais → Autoload*,
     caminho `res://addons/visual_debug/VisualDebugger.cs`, nome `VisualDebug`.
5. **Usar.** Rode o jogo (F5) e aperte Ctrl+F1 a Ctrl+F4.

Para desinstalar, desative o plugin (ele tira o autoload) e apague a pasta.

## Opções

Ficam em *Configurações do Projeto → visual_debug*; ligue *Configurações avançadas* para vê-las.

| Opção | Padrão | O que faz |
| --- | --- | --- |
| `general/enabled_in_release` | desligado | Em build de release o autoload se remove sozinho, a menos que esta opção esteja ligada. |
| `general/start_level` | Off | A camada ligada ao abrir o jogo. |
| `display/font_size` | 11 | O tamanho do texto do overlay. |
| `display/include_internal_nodes` | desligado | Desce também nos filhos internos dos controles, como as barras de um `ScrollContainer`. |
| `trace/capture_on_startup` | desligado | Grava a origem de todo nó desde a abertura do jogo, e não só depois do primeiro Ctrl+F4. |

## Trocar as teclas

As teclas são quatro ações do *Mapa de Entrada*:

- `visual_debug_boxes`
- `visual_debug_names`
- `visual_debug_values`
- `visual_debug_trace`

Quando o projeto não tem essas ações, o addon as cria em tempo de execução com Ctrl+F1 a Ctrl+F4.
Para usar outras teclas, crie as ações com esses nomes no *Mapa de Entrada* do projeto: as teclas
definidas lá valem no lugar das de fábrica.

## Por código

O overlay também pode ser controlado pelo código do jogo:

```csharp
using VisualDebug;

// Liga a camada de valores, como se tivesse apertado Ctrl+F3.
VisualDebugger.Instance!.Level = DebugLevel.Values;

// Grava a origem dos nós da próxima tela que abrir.
VisualDebugger.Instance.CaptureTraces = true;

// Avisa a cada troca de camada.
VisualDebugger.Instance.LevelChanged += level => GD.Print(level);
```

## Como funciona

- **Arquitetura.** `VisualDebugger` é o autoload: lê as teclas e as opções, e põe um `CanvasLayer` na
  camada 128 com o `DebugOverlay`. O `DebugOverlay` é um `Node2D` que, a cada quadro, percorre a
  árvore e desenha tudo no próprio `_Draw()`.
- **Sem alocação.** O percurso usa `GetChildCount`/`GetChild`, que não criam array. Os textos de cada
  nó moram num cartão (`NodeCard`), já medidos, e só são refeitos quando o valor mostrado muda. Com a
  tela parada, o overlay não aloca nada (a barra do topo mostra 0 B). Cada coordenada do mouse vira
  texto uma vez só e fica guardada. Um nó em movimento refaz só a própria linha.
- **Rótulos.** Uma grade grossa (`LabelGrid`) guarda onde já há rótulo. Um rótulo que não cabe livre
  fica de fora, e o nó sob o mouse sempre ganha o seu.
- **Rastreio.** Enquanto a gravação está ligada, o sinal `SceneTree.node_added` lê a pilha
  (`System.Diagnostics.StackTrace`) dentro do próprio `AddChild`. Os quadros do Godot, do .NET e do
  addon são pulados, e o primeiro quadro do projeto é quem pendurou o nó. Com arquivo e linha, a
  leitura é cara, então cada ponto de chamada paga essa leitura completa uma vez e as repetições
  reusam o resultado.

## Limites

- Nós dentro de `SubViewport` e de janelas (`Window`, popups, tooltips) ficam de fora.
- O tooltip do jogo é uma janela e desenha por cima do overlay; por isso o inspetor abre acima do mouse.
- A origem é onde o nó **entrou na árvore**, não onde foi feito o `new`. Uma subárvore montada fora da
  árvore e pendurada de uma vez aponta inteira para o `AddChild` que a pendurou. Nó adicionado por
  `CallDeferred`, ou carregado pelo motor (a cena principal, sem a gravação ligada desde a abertura),
  aparece como `engine`.
- Nó que entrou na árvore antes de a gravação ligar fica com `@ ?`. Para gravá-lo, reabra a tela depois
  do Ctrl+F4, ou ligue `trace/capture_on_startup`.
- Arquivo e linha exigem build de depuração com PDB, que é o padrão ao rodar pelo editor.
- O retângulo de um `TileMapLayer` é aproximado: supõe uma grade quadrada.

## Arquivos

| Arquivo | Papel |
| --- | --- |
| `plugin.cfg`, `VisualDebugPlugin.cs` | O plugin do editor: registra o autoload e as opções. |
| `VisualDebugger.cs` | O autoload: as teclas, as camadas e o `CanvasLayer`. |
| `DebugOverlay.cs` | O `_Draw`: percurso, contornos, rótulos, inspetor e painel. |
| `NodeCard.cs` | O cache de textos e valores de cada nó. |
| `NodeBounds.cs` | O retângulo local de cada tipo de nó. |
| `TraceRecorder.cs` | A gravação da origem no código. |
| `LabelGrid.cs` | A grade que evita rótulos sobrepostos. |
| `TextStyle.cs` | A fonte monoespaçada e a medida das linhas. |
| `DebugSettings.cs` | As opções do projeto. |
| `DebugLevel.cs` | As camadas. |
