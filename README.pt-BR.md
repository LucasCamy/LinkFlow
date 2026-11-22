# LinkFlow

**Português** · [English](README.md)

Controle de ventoinhas, bomba, RGB e display LCD para **Corsair iCUE LINK**, com monitoramento de CPU/GPU (LibreHardwareMonitor), RGB da placa de vídeo (ASUS Aura) e da placa-mãe (MSI Mystic Light).

Este README descreve a **3.2.2**. Os artefatos de desenvolvimento e a distribuição oficial assinada são canais separados.

## Destaques

- **Leve e sem travar**: todo acesso a hardware roda em filas de segundo plano; a interface nunca espera USB/I2C.
- **Serviço do Windows**: o controle continua mesmo sem janela aberta, inicia antes do logon e reinicia sozinho se falhar. A interface não precisa de administrador.
- **Bandeja**: fechar/minimizar mantém o LinkFlow rodando; ícone mostra a temperatura da CPU; menu com perfis.
- **Curva automática** com suavização, histerese e editor arrastável; **modo de segurança** (100% em temperatura crítica ou falha da bomba).
- **Calibração das ventoinhas**: mede cada ventoinha de 100% até 20%, descobre a potência mínima que a mantém girando e avisa quando uma ventoinha passa a girar bem abaixo do normal (poeira ou rolamento gasto).
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
- **Português e inglês**: a interface segue o idioma do Windows. Para trocar, use **Configurações → Geral → Idioma** (Automático, Português ou English); a interface reinicia e, no modo serviço, as ventoinhas não param. O display LCD também é desenhado no idioma escolhido. Logs e o pacote de diagnóstico ficam em português.
- **Backup**: exporte tudo (perfis, curva, nomes, iluminação, LCD com as imagens, limites, preferências) para um arquivo `.linkflow` e importe escolhendo as partes.

## Controle e histórico

**Painel → Controle e histórico** mostra proteção, qualidade das leituras, revisões ativa/salva da configuração, armazenamento e o controle solicitado/aceito pelo controlador/medido em cada ventoinha. Cada sensor informa origem, última aquisição bem-sucedida, idade e validade: válida, vencida, indisponível, inválida ou sem suporte. RPM ausente é desconhecido, não uma ventoinha comprovadamente parada; módulos LCD não oferecem controle de rotação.

- **Proteção independente** acompanha a idade das aquisições mesmo quando o leitor falha ou trava. A abertura inicial dos sensores também tem watchdog; a perda parcial do RPM do hub é acompanhada separadamente das temperaturas.
- **Substituição explícita do sensor da curva**, configurada em **Ventoinhas** para a curva global ou do grupo. O padrão mantém velocidade de proteção e informa a ausência da fonte escolhida. As opções de substituição por CPU/GPU ou CPU/GPU/líquido possuem piso de proteção e indicação visível. O modo de segurança tem prioridade.
- **Resultados dos comandos** distinguem pendência, aceite pelo serviço, aplicação, envio não verificável, rejeição, prazo vencido e conflito de revisão. A confirmação do software/controlador não comprova retorno físico. A gravação é separada: pendente, salva ou com falha. Clientes concorrentes não podem sobrescrever silenciosamente uma revisão que não leram.
- **Histórico local contínuo**, coletado pelo motor com a janela oculta ou sem interface conectada. A tela consulta esse histórico, mantém lacunas e horários reais e mostra mínimo/máximo/média/P95, cobertura válida e tempo acima do alerta. Selecione uma ventoinha, disco ou volume para o histórico individual, compare sessões ou exporte CSV/JSON com aquisição em UTC, unidade, validade, origem e cadência esperada.
- **Armazenamento limitado**: os padrões são coleta a cada 1 segundo, idade máxima de 30 dias e orçamento de 64 MiB em disco, em segmentos de 2 MiB. São limites máximos de retenção: os segmentos mais antigos saem quando qualquer orçamento é atingido. A memória mantém até 7.200 quadros e 2.000 eventos. As leituras caras de disco continuam sob demanda; coleta inativa não comprova saúde do disco. Métricas do processo usam cadência de 30 segundos, e aquisições em cache não contam como novas leituras.
- **Linha do tempo**: alterações de configuração e origem, início/agravamento/resolução de alertas, perda/recuperação de dispositivos, resultados de comandos e mudanças no inventário. Falhas de gravação, registros corrompidos e descartes permanecem visíveis. O inventário compara com a última base válida; operações de manutenção e drivers distinguem processo encerrado, resultado informado, reinício necessário e acompanhamento não confirmado.
- **Diagnóstico**: snapshot com qualidade das leituras, eventos recentes, histórico térmico, operações do Windows e estado cacheado do envio Logitech na sessão do usuário. Aceitação pelo SDK e verificação da luz física ficam separadas. Usuário, perfil e computador são ocultados com as mesmas regras dos logs; revise a exportação antes de compartilhá-la.

Horário silencioso e decisões dos perfis automáticos ficam no motor; a interface informa o contexto da sessão e dos aplicativos. As regras possuem prioridade e intervenção manual com prazo. O controle do hardware é exclusivo entre processos. Se um serviço instalado ficar indisponível, a interface aguarda em modo de leitura, sem abrir um controlador local concorrente. Snapshots vencidos na comunicação também bloqueiam novas ações até a recuperação.

## Instalar

Baixe o **`LinkFlow-Setup-<versão>.exe`** na página de [Releases](https://github.com/LucasCamy/LinkFlow/releases) e execute. Não precisa instalar o .NET.

- O instalador copia o LinkFlow para `Arquivos de Programas`, instala o **serviço do Windows** (recomendado) e, se quiser, abre o LinkFlow na bandeja ao entrar no Windows.
- Pacotes de desenvolvimento são identificados como `LinkFlow-Dev-Setup-<versão>.exe` / `LinkFlow-<versão>-dev-portable.zip` e não alegam publicador verificado. Pacotes oficiais exigem a configuração de assinatura descrita abaixo.
- **Feche o iCUE** antes de usar: os dois não podem controlar o hub ao mesmo tempo.
- Opcional: **RGB das memórias Corsair DDR5** precisa do [PawnIO](https://pawnio.eu/) instalado; **periféricos Logitech** precisam do G HUB aberto com "Permitir que jogos e aplicativos controlem a iluminação" ligado.
- Para remover, use **Configurações do Windows → Aplicativos**.

Também há a versão **portátil** (`LinkFlow-<versão>-portable.zip`): descompacte e abra `LinkFlow.App.exe`.

### Atualizar

Instale a versão 2.3.0 ou posterior por cima da anterior uma vez para ter acesso ao atualizador. As configurações continuam em `%ProgramData%\LinkFlow` e as preferências da interface em `%AppData%\LinkFlow`.

O LinkFlow consulta as releases estáveis do GitHub ao iniciar e periodicamente enquanto estiver aberto, inclusive na bandeja. Você pode desativar essa consulta ou usar **Verificar atualização** em **Configurações → Atualizações**.

- Na versão instalada, escolha **Baixar atualização**. O LinkFlow verifica tamanho e SHA-256. O canal oficial exige também assinatura Authenticode confiável correspondente ao publicador e ao SHA-256 do certificado fixados no aplicativo; o canal de desenvolvimento informa que o publicador não foi verificado. **Instalar e reiniciar** abre o instalador e pode pedir permissão de administrador. A configuração anterior fica registrada; após reabrir, o aplicativo verifica versão/protocolo, comunicação com o serviço e leituras necessárias. Falhas na validação são informadas e permitem restaurar a configuração quando houver confirmação da operação. Isso não reinstala automaticamente binários de uma versão anterior.
- Na versão portátil, escolha **Baixar no navegador** e extraia o ZIP em uma nova pasta.

## Arquitetura

```
LinkFlow.App (WPF, usuário comum)          LinkFlow.Service (Windows Service, SYSTEM)
  Interface, bandeja, atalhos                 LinkFlowEngine  ── hub iCUE LINK (HID)
        │                                          │          ── sensores (LibreHardwareMonitor)
        └──── named pipe "LinkFlow.Engine.v1" ─────┘          ── LCD, ASUS Aura, MSI Mystic Light
              (JSON por linha, ACL: interativo/admin/SYSTEM)
```

- **Modo serviço** (recomendado): a interface conecta ao serviço pelo pipe.
- **Modo local**: sem serviço instalado, o motor roda dentro do aplicativo (como administrador para ler a temperatura da CPU). Um serviço instalado, mas sem comunicação, continua sendo o proprietário; a interface reconecta em modo de leitura.
- `LinkFlow.Core`: engine, protocolo do hub, configuração (`%ProgramData%\LinkFlow\config.json`), IPC e hardware simulado.
- O nome do pipe continua `LinkFlow.Engine.v1` para descoberta, mas o protocolo negociado é **v2**. Ele transporta comandos/resultados identificados e consultas limitadas de histórico, eventos, sessões, comparação e CSV/JSON. As respostas têm limite de 1 MiB; as consultas RPC permitem até 1.200 pontos ou 200 eventos. Versões incompatíveis não podem escrever; configurações com esquema posterior ao suportado são rejeitadas.

## Computadores pareados (Fleet)

**Painel → Computadores** acompanha até 32 agentes pareados explicitamente, consulta histórico e aplica um perfil autorizado. Cada destino mantém seu motor e suas proteções sem conexão de rede. O Fleet fica **desligado até o provisionamento**; o padrão permite apenas observação em loopback. Escuta pela rede e controle remoto são opções separadas. A conexão usa TLS 1.2/1.3, SHA-256 de certificado pareado explicitamente e credenciais aleatórias com escopo. O controle remoto não pode alterar limites de segurança nem a política de substituição das curvas; ações que exigem a sessão local são recusadas.

No **computador de destino**, abra um PowerShell elevado na pasta publicada do LinkFlow:

```powershell
.\LinkFlow.Cli.exe fleet init --name "Desktop"
# Opcional: observação e controle na LAN em um endereço local explícito:
# .\LinkFlow.Cli.exe fleet init --name "Desktop" --bind 192.168.1.10 --allow-network --allow-control
```

O `init` cria `fleet\agent.json` e `agent.pfx` em uma pasta privada, entrega a impressão do certificado e a credencial de observação (mais uma credencial separada de controle, somente se solicitada) e recusa substituir um pareamento existente. Mantenha essa saída de credenciais em local privado. Reinicie o serviço pelas Configurações para carregar o agente. O comando não abre portas no firewall; configure separadamente o acesso de rede pretendido.

No **computador de acompanhamento**, use a aba Computadores ou um console do usuário comum:

```powershell
.\LinkFlow.Cli.exe fleet add-peer --peer desktop.json --name "Desktop" --host 192.168.1.10 --pin <SHA256_DO_CERTIFICADO>
# Digite a credencial na entrada oculta. Ela também pode vir pela entrada padrão, nunca como argumento.
.\LinkFlow.Cli.exe fleet monitor --peer desktop.json
.\LinkFlow.Cli.exe fleet history --peer desktop.json --metric cpu.temp --minutes 60
.\LinkFlow.Cli.exe fleet profile --peer desktop.json --profile Equilibrado
```

O comando de perfil exige credencial com escopo de controle. As chaves dos perfis prontos continuam `Silencioso`, `Equilibrado`, `Desempenho`, `Máximo` e `Automático`; os rótulos da interface seguem o idioma escolhido. `fleet command --peer desktop.json --file comando.json` envia um `EngineCommandRequest` identificado, sujeito aos mesmos limites de revisão, prazo e proteção. Um resultado inicial de aceite não comprova que todos os dispositivos chegaram fisicamente ao estado pedido.

As credenciais dos pares são protegidas pelo **DPAPI do usuário do Windows que as salvou**. A lista da interface e os arquivos individuais da CLI são separados; copiá-los para outra conta não transfere credenciais utilizáveis. O agente guarda hashes dos tokens e protege a senha do certificado para a máquina, em pasta restrita a administradores/SYSTEM. Uma troca de certificado exige novo pareamento explícito. O certificado TLS gerado localmente para o Fleet é separado da identidade Authenticode usada na assinatura das versões oficiais.

## Compilar e publicar

Requisitos: Windows e .NET SDK 10. Os scripts usam PowerShell; o executor de repetição exige PowerShell 7.2 ou posterior.

```powershell
dotnet build LinkFlow.slnx --configuration Release -warnaserror
dotnet test tests\LinkFlow.Tests --configuration Release --no-build
.\scripts\publish.ps1                 # App + Service + CLI dependentes do .NET em bin\LinkFlow_v2
.\scripts\soak.ps1 -Repeat 5 -NoBuild
# Ou repita as verificações isoladas por pelo menos dez minutos:
# .\scripts\soak.ps1 -DurationSeconds 600 -NoBuild
```

`Atualizar-E-Executar.bat` compila, para o serviço (se instalado), atualiza `bin\LinkFlow` e reabre o app.

### Preparar um release

Use a mesma versão `X.Y.Z` nos projetos App, Core, Service e CLI. O empacotamento de desenvolvimento é o padrão e exige [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
.\scripts\release.ps1 -Distribution Development
# -> artifacts\development: instalador dev e ZIP portátil com .NET, hashes e release-manifest.json
```

O empacotamento oficial exige certificado de assinatura de código válido com chave privada em `Cert:\CurrentUser\My`, SignTool do Windows SDK e identidade exata de publicador/certificado:

```powershell
.\scripts\release.ps1 -Distribution Official `
    -CertificateThumbprint <IMPRESSAO_NA_STORE> `
    -ExpectedPublisher "Seu publicador verificado" `
    -ExpectedCertificateSha256 <SHA256_DO_CERTIFICADO>
# -> artifacts\release; binários do aplicativo, instalador e desinstalador assinados e verificados
```

O repositório não inclui certificado oficial nem chave privada. Dados de assinatura ausentes bloqueiam o pacote oficial, em vez de gerar um artefato oficial sem assinatura. Os seletores também podem vir de `LINKFLOW_SIGNING_THUMBPRINT`, `LINKFLOW_SIGNING_PUBLISHER` e `LINKFLOW_SIGNING_CERT_SHA256`. `-TimestampServer` aceita um servidor de carimbo de tempo HTTPS.

O envio de uma tag `vX.Y.Z` executa o workflow oficial assinado. Ele exige os secrets `LINKFLOW_SIGNING_PFX_BASE64` e `LINKFLOW_SIGNING_PFX_PASSWORD` e as variables `LINKFLOW_SIGNING_PUBLISHER` e `LINKFLOW_SIGNING_CERT_SHA256` no repositório. Revise os arquivos assinados, `SHA256SUMS.txt` e `release-manifest.json` antes de publicar o rascunho.

Uma publicação sem assinatura exige execução manual explícita do workflow com `distribution=Development` e `release_tag=vX.Y.Z`. Ela continua sendo um build Development mesmo quando publicada como versão estável no GitHub: o instalador e o manifesto informam que o publicador não foi verificado. Os aliases de compatibilidade `LinkFlow-Setup-X.Y.Z.exe` e `LinkFlow-X.Y.Z-portable.zip` têm os mesmos bytes dos arquivos Development e constam em `SHA256SUMS.txt`. Isso permite que atualizadores antigos encontrem o instalador; a verificação de assinatura dos builds Official permanece obrigatória. A falta de certificado nunca converte silenciosamente uma solicitação Official em Development.

### Verificação repetível de confiabilidade

`scripts\soak.ps1` executa apenas `ReliabilityIntegrationTests`, em processos novos com dados, temporários e nomes de pipe isolados. Verifica falhas repetidas de sensores/RPM, recuperação da proteção, resultados de comandos, persistência, conflitos e limites das filas. Cada repetição precisa executar o mesmo conjunto não vazio de testes, sem falhas nem testes ignorados. `-IterationTimeoutSeconds` vale 120 por padrão; processos criados pelo executor e seus descendentes são encerrados em caso de prazo excedido. TRX, saída padrão/erros, logs de execução limitados e `summary.json` ficam em `artifacts\soak` ou em `-ResultsDirectory`.

São verificações de integração repetidas com atuadores simulados e IPC/arquivos locais reais. Isso não certifica 72 horas contínuas em hardware físico, compatibilidade de drivers nem desempenho térmico; essas validações dependem de cada equipamento.

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
| `--peripheral-rgb-diagnostics` | Diagnóstico opcional do envio de iluminação Logitech na sessão do usuário |
| `--lang pt-BR` / `--lang en` | Idioma da interface só nesta execução, sem mudar o salvo |
| `--screenshot arquivo.png [--page N] [--size LxA] [--detail cpu] [--select N]` | Renderiza a janela num PNG e sai (teste visual) |

### Tradução

Os textos da interface ficam em `src/LinkFlow.App/Resources/Strings.resx` (português) e `Strings.en.resx` (inglês). Os textos do serviço ficam em `src/LinkFlow.Core/Resources/EngineStrings.resx` e `EngineStrings.en.resx`, por código. Os helpers das telas operacional, de confiabilidade e Fleet também fornecem os dois idiomas. Os testes verificam paridade dos recursos e comportamento da apresentação.

`LinkFlow.Cli.exe status` consulta o serviço instalado sem abrir hardware. `--diagnose` abre explicitamente o diagnóstico direto e exige controle exclusivo, sem serviço em execução. `LinkFlow.Cli.exe --help` lista os comandos Fleet.

`LinkFlow.Service.exe --simulate --no-fleet` inicia um host em console com atuadores simulados e sem iniciar o agente de rede. Defina **LINKFLOW_DATA_DIR e um LINKFLOW_PIPE exclusivo** antes de uma simulação manual, para não compartilhar dados ou o endereço de produção; `LINKFLOW_DEBUG=1` habilita log detalhado. O executor de repetição isola tudo automaticamente. `--detail operations` seleciona a aba operacional nos testes de screenshot; `--detail fleet` seleciona Computadores.
Para capturar o controle por dispositivo sem seleção, use `--page 1 --detail devices`; acrescente `--select 2` para comparar o estado habilitado.

Ao iniciar uma nova instância da interface, `--peripheral-rgb-diagnostics` grava um único arquivo de estado atual limitado em `%AppData%\LinkFlow\diagnostics\peripheral-rgb-<PID>.json`, no máximo uma vez por segundo e fora da thread de envio. Registra cores calculadas, andamento das chamadas e resultados do SDK; não verifica a luz física nem abre outra sessão de controle. Simulações e capturas de tela não ativam esse envio.

A publicação substitui o arquivo existente de forma atômica e preserva o detalhe, horário, etapa e HResult da última falha de gravação mesmo após recuperação. Esses erros de diagnóstico são separados dos resultados do envio RGB. Leitores concorrentes devem permitir `FileShare.ReadWrite | FileShare.Delete`, copiar os bytes e fechar o arquivo antes de interpretar o JSON.

## Arquivos

- Configuração: `%ProgramData%\LinkFlow\config.json`
- Logs: `%ProgramData%\LinkFlow\logs\linkflow-{app|service}-AAAA-MM-DD.log`
- Preferências da interface: `%AppData%\LinkFlow\ui.json`
- Imagens/GIFs do LCD: `%ProgramData%\LinkFlow\media` (copiadas ao adicionar, para o serviço conseguir ler)
- Histórico e eventos: `%ProgramData%\LinkFlow\history\lfh-*.jsonl`
- Base do inventário e automação: `%ProgramData%\LinkFlow\inventory.json`, `automation-state.json`
- Agente Fleet privado opcional: `%ProgramData%\LinkFlow\fleet\agent.json`, `agent.pfx`
- Pares e operações do usuário: `%AppData%\LinkFlow\fleet-peers.json`, `system-operations.json`
- Recuperação de atualização: `%AppData%\LinkFlow\updates\update-recovery.json`
