# LinkFlow

Controle de ventoinhas, bomba, RGB e display LCD para **Corsair iCUE LINK**, com monitoramento de CPU/GPU (LibreHardwareMonitor), RGB da placa de vídeo (ASUS Aura) e da placa-mãe (MSI Mystic Light).

## Destaques

- **Leve e sem travar**: todo acesso a hardware roda em filas de segundo plano; a interface nunca espera USB/I2C.
- **Serviço do Windows**: o controle continua mesmo sem janela aberta, inicia antes do logon e reinicia sozinho se falhar. A interface não precisa de administrador.
- **Bandeja**: fechar/minimizar mantém o LinkFlow rodando; ícone mostra a temperatura da CPU; menu com perfis.
- **Curva automática** com suavização, histerese e editor arrastável; **modo de segurança** (100% em temperatura crítica ou falha da bomba).
- **Seleção múltipla** de ventoinhas (Ctrl/Shift+clique), ações em lote, grupos, renomear, perfis salvos e **Desfazer (Ctrl+Z)**.
- **Atalhos** locais e globais (funcionam em jogos).
- **Painel com detalhes**: clique em CPU, GPU, líquido, ventoinhas ou memória para ver histórico de até 10 minutos, mínimo/máximo/média, clock, potência, VRAM e a lista de dispositivos.
- **Seletor de cor completo**: quadro de saturação × brilho com barra de matiz, código hex e ajuste fino RGB.
- **LCD personalizável**: monte o quadro (informação principal, até 4 secundárias, título, anel, foto de fundo) ou mostre fotos e GIFs com apresentação de slides, rotação e informação sobreposta. A prévia usa o mesmo renderizador do display.
- **Reatualizar dispositivos**: botão "Atualizar" em Ventoinhas procura de novo hub, ventoinhas, LCD e RGB.
- **Hardware** mostra um inventário filtrável de CPU, GPUs, placa-mãe, BIOS, versão do Windows, memória, discos, rede, monitores e periféricos. A leitura mostra geração e velocidade da RAM e diferencia SSD, HDD e barramentos como NVMe, SATA e USB quando o Windows fornece esses dados. O catálogo indica as funções de cada adaptador do LinkFlow.
- **Desempenho do Windows** permite ativar planos de energia, ajustar os tempos para apagar a tela e suspender o PC e alternar as animações da interface. A aba permite desfazer o último ajuste feito na sessão e remover apps opcionais da conta após confirmação.
- **Drivers** consulta os drivers de dispositivo oferecidos pelo Windows Update quando você clica em **Buscar atualizações**. Você pode revisar e confirmar a instalação de cada driver. O Windows solicita elevação quando necessário e o LinkFlow informa se for preciso reiniciar.
- **Fabricantes** compara os drivers GeForce e os pacotes dos chipsets AMD AM4 e AM5 reconhecidos com os catálogos oficiais. Em **Drivers instalados**, filtre dispositivos conectados por versão, fabricante, INF ou ID de hardware. Quando o Windows fornece o ID, a aba abre a busca correspondente no Catálogo Microsoft Update. O AMD Auto-Detect e o Intel Driver & Support Assistant estão disponíveis para os dispositivos detectados.
- **Widget flutuante configurável** (aba Widget) com CPU, GPU, ventoinhas, rede, discos e outros sensores em linha, coluna ou grade. Pode ficar sempre no topo, deixar cliques passarem e ocultar em tela cheia somente no próprio monitor.
- **Overlay em jogos (RTSS)** em **Widget → Comportamento**. Instale e abra o [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/), habilite o OSD no perfil do jogo e ative a opção com o widget ligado. O LinkFlow envia as leituras selecionadas até uma vez por segundo. O RTSS define o visual e a posição do texto e pode exibi-lo em tela cheia exclusiva em jogos compatíveis. Alguns jogos restringem overlays pelo anti-cheat.
- **Rede e discos**: download/upload, espaço por volume e — só quando algo exibe — atividade, leitura/escrita e temperatura dos discos.
- **Curvas por grupo**: um grupo pode seguir a própria curva e o próprio sensor (ex.: radiador pelo líquido).
- **Perfil automático**: por jogo/app aberto e horário silencioso; volta sozinho ao perfil anterior.
- **Periféricos Logitech G** pelo G HUB (a interface aplica a cor, pois o SDK só atende a sessão do usuário) e **cor viva nos LEDs** (correção de gama + brilho por dispositivo).
- **Leve**: discos e prévias só leem quando visíveis; na bandeja a interface devolve a memória ao Windows; dispositivos USB reconectam na hora ao serem ligados.
- **Backup**: exporte tudo (perfis, curva, nomes, iluminação, LCD com as imagens, limites, preferências) para um arquivo `.linkflow` e importe escolhendo as partes.

## Instalar

Baixe o **`LinkFlow-Setup-<versão>.exe`** na página de [Releases](https://github.com/LucasCamy/LinkFlow/releases) e execute. Não precisa instalar o .NET.

- O instalador copia o LinkFlow para `Arquivos de Programas`, instala o **serviço do Windows** (recomendado) e, se quiser, abre o LinkFlow na bandeja ao entrar no Windows.
- O Windows pode mostrar "O Windows protegeu o computador" (o instalador não é assinado): clique em **Mais informações → Executar assim mesmo**.
- **Feche o iCUE** antes de usar: os dois não podem controlar o hub ao mesmo tempo.
- Opcional: **RGB das memórias Corsair DDR5** precisa do [PawnIO](https://pawnio.eu/) instalado; **periféricos Logitech** precisam do G HUB aberto com "Permitir que jogos e aplicativos controlem a iluminação" ligado.
- Para atualizar, rode o instalador da versão nova por cima (as configurações ficam em `%ProgramData%\LinkFlow`). Para remover, use **Configurações do Windows → Aplicativos**.

Também há a versão **portátil** (`LinkFlow-<versão>-portable.zip`): descompacte e abra `LinkFlow.App.exe`.

## Arquitetura

```
LinkFlow.App (WPF, usuário comum)          LinkFlow.Service (Windows Service, SYSTEM)
  Interface, bandeja, atalhos                 LinkFlowEngine  ── hub iCUE LINK (HID)
        │                                          │          ── sensores (LibreHardwareMonitor)
        └──── named pipe "LinkFlow.Engine.v1" ─────┘          ── LCD, ASUS Aura, MSI Mystic Light
              (JSON por linha, ACL: interativo/admin/SYSTEM)
```

- **Modo serviço** (recomendado): a interface conecta ao serviço pelo pipe.
- **Modo local**: sem serviço, o engine roda dentro do próprio app (como administrador para ter temperatura da CPU).
- `LinkFlow.Core`: engine, protocolo do hub, configuração (`%ProgramData%\LinkFlow\config.json`), IPC e hardware simulado.

## Compilar e publicar

Requisitos: .NET SDK 10.

```powershell
dotnet build LinkFlow.slnx
dotnet test tests\LinkFlow.Tests
.\scripts\publish.ps1                 # gera bin\LinkFlow_v2 com LinkFlow.App.exe + LinkFlow.Service.exe
```

`Atualizar-E-Executar.bat` compila, para o serviço (se instalado), atualiza `bin\LinkFlow` e reabre o app.

### Preparar um release

Use a mesma versão `X.Y.Z` nos projetos `LinkFlow.App` e `LinkFlow.Service`. Envie a tag `vX.Y.Z` para o GitHub apontando para o commit dessa versão. O GitHub Actions executa os testes no Windows e anexa o instalador x64, o ZIP portátil e `SHA256SUMS.txt` a um rascunho em **Releases**. Revise o rascunho e clique em **Publish release** para disponibilizar os downloads.

Para gerar os mesmos arquivos localmente, rode `.\scripts\release.ps1` com o [Inno Setup 6](https://jrsoftware.org/isinfo.php) instalado. Os arquivos ficam em `artifacts\release`.

## Instalar o serviço

Abra o LinkFlow → **Configurações → Serviço do Windows → Instalar serviço** (pede UAC uma vez). Para remover, use **Remover serviço** no mesmo lugar.

Manualmente (PowerShell como administrador):

```powershell
.\LinkFlow.App.exe --install-service      # cria, configura reinício automático e inicia
.\LinkFlow.App.exe --uninstall-service
```

## Atalhos

| Atalho | Ação |
|---|---|
| F1 … F5 | Perfis: Silencioso, Equilibrado, Desempenho, Máximo, Curva |
| Ctrl+Alt+1 … 5 | Perfis de qualquer lugar (globais) |
| Ctrl+Alt+L / Ctrl+Alt+M | Abrir/esconder o LinkFlow / mini widget (globais) |
| Ctrl+1 … 9 | Páginas (Ctrl+5 = Widget, Ctrl+6 = Configurações, Ctrl+7 = Hardware, Ctrl+8 = Windows, Ctrl+9 = Drivers) |
| Ctrl+↑ / Ctrl+↓ | Potência global ±5% |
| Ctrl+Z | Desfazer |
| Ctrl+A / Esc | Selecionar todas / limpar seleção (Ventoinhas) |
| Ctrl+R | Renomear o cartão selecionado (também: lápis, clique duplo ou botão direito) |
| Ctrl+M / Ctrl+L | Mini widget / enviar quadro ao LCD |
| Esc, Ctrl+W / Ctrl+Q | Esconder na bandeja / sair |

## Linha de comando

| Argumento | Efeito |
|---|---|
| `--tray` | Inicia escondido na bandeja (usado pelo início automático) |
| `--exit` | Encerra a instância em execução de forma controlada |
| `--simulate` | Hardware simulado (testes/demonstração, não toca na configuração real) |
| `--screenshot arquivo.png [--page N] [--size LxA] [--detail cpu] [--select N]` | Renderiza a janela num PNG e sai (teste visual) |

`LinkFlow.Service.exe --simulate` roda o serviço em console com hardware simulado. Variáveis `LINKFLOW_PIPE`, `LINKFLOW_DATA_DIR` e `LINKFLOW_DEBUG=1` isolam testes e ativam log detalhado.

## Arquivos

- Configuração: `%ProgramData%\LinkFlow\config.json`
- Logs: `%ProgramData%\LinkFlow\logs\linkflow-{app|service}-AAAA-MM-DD.log`
- Preferências da interface: `%AppData%\LinkFlow\ui.json`
- Imagens/GIFs do LCD: `%ProgramData%\LinkFlow\media` (copiadas ao adicionar, para o serviço conseguir ler)
