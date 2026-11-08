# LinkFlow

Controle de ventoinhas, bomba, RGB e display LCD para **Corsair iCUE LINK**, com monitoramento de CPU/GPU (LibreHardwareMonitor), RGB da placa de vídeo (ASUS Aura) e da placa-mãe (MSI Mystic Light).

## Destaques

- **Leve e sem travar**: todo acesso a hardware roda em filas de segundo plano; a interface nunca espera USB/I2C.
- **Serviço do Windows**: o controle continua mesmo sem janela aberta, inicia antes do logon e reinicia sozinho se falhar. A interface não precisa de administrador.
- **Bandeja**: fechar/minimizar mantém o LinkFlow rodando; ícone mostra a temperatura da CPU; menu com perfis.
- **Curva automática** com suavização, histerese e editor arrastável; **modo de segurança** (100% em temperatura crítica ou falha da bomba).
- **Seleção múltipla** de ventoinhas (Ctrl/Shift+clique), ações em lote, grupos, renomear, perfis salvos e **Desfazer (Ctrl+Z)**.
- **Atalhos** locais e globais (funcionam em jogos).

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
| Ctrl+1 … 5 | Páginas |
| Ctrl+↑ / Ctrl+↓ | Potência global ±5% |
| Ctrl+Z | Desfazer |
| Ctrl+A / Esc | Selecionar todas / limpar seleção (Ventoinhas) |
| Ctrl+M / Ctrl+L | Mini widget / enviar quadro ao LCD |
| Esc, Ctrl+W / Ctrl+Q | Esconder na bandeja / sair |

## Linha de comando

| Argumento | Efeito |
|---|---|
| `--tray` | Inicia escondido na bandeja (usado pelo início automático) |
| `--exit` | Encerra a instância em execução de forma controlada |
| `--simulate` | Hardware simulado (testes/demonstração, não toca na configuração real) |
| `--screenshot arquivo.png [--page N] [--size LxA]` | Renderiza a janela num PNG e sai (teste visual) |

`LinkFlow.Service.exe --simulate` roda o serviço em console com hardware simulado. Variáveis `LINKFLOW_PIPE`, `LINKFLOW_DATA_DIR` e `LINKFLOW_DEBUG=1` isolam testes e ativam log detalhado.

## Arquivos

- Configuração: `%ProgramData%\LinkFlow\config.json`
- Logs: `%ProgramData%\LinkFlow\logs\linkflow-{app|service}-AAAA-MM-DD.log`
- Preferências da interface: `%AppData%\LinkFlow\ui.json`
