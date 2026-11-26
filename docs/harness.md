# Harness de teste do SampleDrum (4 peças, sem mesa e sem ASIO)

O harness deixa testar o SampleDrum v2 num PC Windows **sem a X32 e sem nenhum driver ASIO**. Ele usa exatamente o
mesmo motor do modo ASIO (`KitEngine`, bumbo/caixa/tom/surdo) e o mesmo roteamento por peça (perfil **Simulação**:
entradas USB 3/4/5/6 → saídas USB 1/2/3/4), e gera arquivos e relatórios que dá para ouvir e conferir.

Há três formas de usar:

| Forma | Para quê |
|---|---|
| `SampleDrum.App.exe --harness <pasta>` | roda o cenário de teste, grava os retornos e o relatório, código de saída 0 = tudo certo |
| `SampleDrum.App.exe --selftest` | verificações automáticas rápidas (≈ 7 s), código de saída 0 = tudo certo |
| `SampleDrum.App.exe --simulation` | abre a janela já no perfil Simulação com o kit sintético (ver "Limitações") |

O mesmo cenário também roda no console multiplataforma: `SampleDrum.Offline --kit <pasta>` (sem a parte em tempo real).

## Comandos

### A partir do código-fonte (SDK .NET 10)

```powershell
dotnet build SampleDrum.sln -c Release
dotnet test SampleDrum.sln -c Release --no-build

# verificações automáticas
src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --selftest

# cenário de teste (offline) + rodada em tempo real; relatórios em C:\temp\sd-teste
src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --harness C:\temp\sd-teste --realtime

# o mesmo, ouvindo a prévia no fone/caixas do PC
src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --harness C:\temp\sd-teste --play

# console multiplataforma (também roda no WSL/Linux), bloco de 480 e outra semente
dotnet run --project src\SampleDrum.Offline -c Release -- --kit C:\temp\sd-offline --block 480 --seed 7
```

No PowerShell, um programa de janela (WinExe) não segura o prompt: para ver a saída e o código de saída use
`| Out-Host` ou `Start-Process -Wait`, por exemplo:

```powershell
.\SampleDrum.App.exe --harness C:\temp\sd-teste | Out-Host; $LASTEXITCODE
```

(No Prompt de Comando use `start /wait SampleDrum.App.exe --selftest` e `echo %ERRORLEVEL%`.)

### Gerar uma pasta publicada (não precisa de SDK no PC de destino)

```powershell
dotnet publish src\SampleDrum.App -c Release -r win-x64 --self-contained -o C:\temp\SampleDrum-publicado
```

A pasta tem ~120 MB e roda sozinha em qualquer Windows 10/11 x64:

```powershell
cd C:\temp\SampleDrum-publicado
.\SampleDrum.App.exe --selftest | Out-Host
.\SampleDrum.App.exe --harness C:\temp\sd-teste --play | Out-Host
.\SampleDrum.App.exe --list-drivers | Out-Host
```

Não publique dentro do repositório (a pasta não deve ser versionada).

### Opções do `--harness` / `--kit`

| Opção | Efeito |
|---|---|
| *(nenhuma)* | gera o cenário sintético de 4 peças (com gabarito) |
| `--mics a.wav b.wav c.wav d.wav` | usa os seus 4 WAVs de microfone, na ordem Bumbo, Caixa, Tom, Surdo (sem gabarito: o relatório só lista as detecções) |
| `--mics4 multi.wav [--channels 1,2,3,4]` | um WAV multicanal; `--channels` diz qual canal (1-based) é de cada peça |
| `--block N` | tamanho do bloco: 64, 128, 256 (padrão) ou 480 |
| `--seed S` | semente do cenário sintético (padrão 1); mesma semente = mesmos arquivos |
| `--tolerance ms` | tolerância de posição para casar esperado × detectado (padrão 5 ms) |
| `--realtime` | também passa o cenário pelo host de simulação em tempo real (só `SampleDrum.App.exe`) |
| `--play` | idem, tocando a prévia estéreo na saída padrão do PC |

Código de saída: **0** = todas as expectativas passaram (com microfones próprios: rodou até o fim); **1** = alguma
expectativa falhou; **2** = erro de argumento ou de arquivo (mensagem em português).

WAVs de entrada podem ter qualquer taxa e ser mono ou estéreo: são convertidos para mono a 48 kHz antes de tocar
(o relatório e o console avisam quando houve conversão).

## Arquivos gerados

| Arquivo | Conteúdo |
|---|---|
| `retorno-bumbo.wav`, `retorno-caixa.wav`, `retorno-tom.wav`, `retorno-surdo.wav` | o retorno de cada peça exatamente como saiu na saída USB roteada dela (mono, 48 kHz, float) |
| `retornos-4ch.wav` | as saídas do PC lado a lado: **canal n = saída USB n** (no perfil Simulação: 1 Bumbo, 2 Caixa, 3 Tom, 4 Surdo) |
| `previa-estereo.wav` | a mistura para fone: os 4 retornos com pan fixo (Bumbo no centro, Caixa um pouco à esquerda, Tom à direita, Surdo mais à direita) e os microfones 18 dB abaixo |
| `mic-bumbo.wav` … `mic-surdo.wav`, `mics-4ch.wav` | as entradas usadas (o cenário sintético ou os seus WAVs); dá para reusar com `--mics`/`--mics4` |
| `relatorio.csv` | uma linha por evento: peça, entrada, saída, posição esperada, posição detectada, erro em amostras e em ms, velocidade, status, se devia disparar, descrição |
| `relatorio.md` | o mesmo em Markdown, com o resumo por peça e o cabeçalho da rodada (bloco, semente, roteamento, limiares) |
| `tempo-real\…` | os mesmos arquivos da rodada em tempo real (`--realtime`/`--play`) |

Status no relatório: **ok** (disparou onde devia, na saída certa; ou não disparou onde não devia), **faltando**
(devia disparar e não disparou), **extra** (disparou sem dever: vazamento, segunda batida dentro do intervalo mínimo
ou disparo inesperado) e **saída-errada** (disparou, mas o sample não saiu na saída USB daquela peça).

## Como ouvir a prévia

* `--play` toca a rodada em tempo real na **saída de áudio padrão do Windows** (a que estiver selecionada no ícone de
  som: fone ou caixas) por ~15 s. Não usa ASIO e não toca nada na mesa. Ao fim mostra quantas faltas de dados
  (underruns) e blocos descartados houve; o normal é 0 e 0.
* Ou abra `previa-estereo.wav` em qualquer player. A prévia tem os retornos em volume cheio (−6 dB de master) e os
  microfones 18 dB abaixo, para comparar o sample com a batida original.
* A prévia usa NAudio `WaveOut` com ~200 ms de atraso (buffer), o que não importa para ouvir: a latência do motor
  é a mesma do modo ASIO e não depende disso.

## Como conferir o roteamento com as saídas separadas

1. Abra `retornos-4ch.wav` num editor de áudio que mostre canais separados (por exemplo o Audacity, opção de dividir
   a faixa estéreo/multicanal em mono). Cada canal é uma saída USB do PC: canal 1 = USB 1 (→ Card In 1 na mesa).
2. Cada batida do bumbo em `mic-bumbo.wav` deve ter um sample **só no canal 1**; caixa só no 2; tom só no 3; surdo só
   no 4. Nas batidas simultâneas aparecem vários canais ao mesmo tempo, cada um com o seu sample (timbres diferentes).
3. O relatório já faz essa conferência automaticamente: qualquer sample que saia na saída errada vira **saída-errada**
   e o resultado geral falha.
4. Os arquivos `retorno-<peça>.wav` são os mesmos sinais separados por peça (útil para escutar uma peça de cada vez).

## O cenário sintético

Quatro trilhas de microfone a 48 kHz, 14,5 s, determinísticas pela semente (ruído de fundo −60 dBFS em cada trilha):

| Trecho | O que testa |
|---|---|
| 0,5–2,0 s | uma batida forte isolada por peça |
| 2,5–4,0 s | uma batida suave isolada por peça (≈ 8–12 dB acima do limiar) |
| 4,8–6,0 s | uma batida por peça começando 1–7 amostras antes de uma fronteira de bloco comum a 32/64/128/256/480 (múltiplos de 3840 amostras) |
| 6,6 / 7,2 / 7,8 s | batidas simultâneas na mesma amostra: 2 peças, 3 peças, 4 peças |
| 8,5–10,7 s | duplas por peça com 120 ms (fora do intervalo mínimo): as duas devem disparar |
| 11,3–13,4 s | duplas por peça com 20 ms (dentro do intervalo mínimo): só a primeira deve disparar |
| o tempo todo | vazamento controlado: bumbo → microfone do surdo (−18 dB, +1 ms) e caixa → microfone do tom (−15 dB, +0,5 ms), que **não** devem disparar surdo/tom |

Cada peça tem um timbre de microfone e um sample sintético diferentes (bumbo com varredura 120→50 Hz, caixa com
ruído + tom, tom ~190→140 Hz, surdo ~120→85 Hz), para os retornos serem fáceis de distinguir de ouvido.

Detecção usada no cenário: os padrões por peça do Core (`KitDefaults`: Bumbo −24, Caixa −20, Tom −20 dBFS, com os
filtros e tempos de cada peça), **exceto o Surdo, com limiar −16 dBFS em vez de −22**. Motivo, medido no harness:
com −22 dBFS, uma batida forte de bumbo (−3 dBFS) vazando a −18 dB chega a ≈ −21 dBFS no microfone do surdo e o
dispara (1 disparo extra). Isto é uma **nota de calibração para o ensaio**: o motor não tem "gate" entre canais na
v2, então o limiar de cada tom precisa ficar acima do vazamento mais forte que o microfone dele recebe.
Com `--mics` (seus WAVs) o harness usa os padrões do Core sem alteração.

## Simulação na janela (`--simulation`) e API para a interface

`SampleDrum.App.exe --simulation` liga `SampleDrum.App.Harness.StartupOptions.SimulationMode`. A janela v2 deve então
selecionar o perfil **Simulação**, o host "Simulação (sem ASIO)" e aplicar o kit sintético
(`StartupOptions.SimulationKit()`, `StartupOptions.SimulationKitSettings()`, `StartupOptions.SimulationHostOptions`).

O host de simulação (`FakeAudioHost`) é criado como o host ASIO: `new FakeAudioHost(kitEngine, routing, opções)`.
Opções (`FakeHostOptions`): fonte (`SyntheticKit`, `MicWavs` com 4 caminhos, `MultichannelWav` com caminho e canais),
semente, bloco (`FakeHostOptions.BlockSizes` = 64/128/256/480), loop, prévia ligada, `Preview`
(`PreviewMixSettings`: volume em dB, quais peças entram na prévia, nível dos microfones ou `null` para não incluir).
Com o host rodando dá para mudar `host.PreviewEnabled` e `host.Preview`. Medidores: `host.ReadAndResetOutputPeak(peça)`.
Peças sem entrada roteada recebem silêncio e retornos sem saída roteada são descartados, igual ao host ASIO.

## API do NAudio usada na prévia (conferida na versão instalada)

Pacote `NAudio.WinMM` **3.3.0** (MIT), conferido no XML do pacote e no código-fonte da tag `v3.3.0`
(`src/NAudio.WinMM/WaveOut.cs`, `src/NAudio.Core/Wave/WaveOutputs/IWaveProvider.cs`):

* No NAudio 3.0 o antigo `WaveOutEvent` foi **renomeado para `WaveOut`** (`WaveOutEvent` continua como subclasse
  marcada `[Obsolete]`). `WaveOut` usa callback por evento e uma **thread própria** de reprodução (não precisa de
  janela/loop de mensagens). Padrões: `BufferMilliseconds = 100`, `NumberOfBuffers = 2`, `DeviceNumber = -1`
  (= WAVE_MAPPER, a saída padrão do Windows). O SampleDrum usa 3 × 40 ms + 80 ms de pré-buffer.
* `IWaveProvider.Read` agora recebe `Span<byte>` (no NAudio 2.x era `byte[] buffer, int offset, int count`).
* `WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)` para float estéreo; o mapeador do WinMM converte para o formato do
  dispositivo se precisar.
* A prévia **nunca** está no caminho do ASIO: o host de simulação escreve num anel lock-free (um produtor, um
  consumidor); a thread do `WaveOut` lê dele. Se faltar dado, toca silêncio e conta um underrun; se o anel encher,
  o bloco novo é descartado e conta um overrun. O motor nunca espera pela placa de som.

## Verificações executadas (25/11/2026, branch `ao/asio-app/root`, Release, neste PC sem X32 e sem ASIO)

| Verificação | Resultado |
|---|---|
| `dotnet build SampleDrum.sln -c Release` | 0 avisos, 0 erros |
| `dotnet test SampleDrum.sln -c Release` | Core: 215/215 (inclui `HarnessTests`); App (Windows): 7/7 (`HarnessRealtimeTests`) |
| `SampleDrum.App.exe --selftest` | 11/11 OK em ~7 s, código 0 |
| `--harness <pasta>` com bloco 64, 128, 256 e 480 | OK nos quatro, código 0 |
| `--harness <pasta> --realtime` (tempo real 1×) | offline OK e tempo real OK, disparos idênticos ao offline |
| `--harness <pasta> --play` | OK; saída padrão aberta, 0 underruns, 0 descartes (o áudio não foi escutado por quem rodou) |
| `--harness --mics a b c d` e `--mics4 mics-4ch.wav --block 64` | rodaram até o fim (sem gabarito), código 0 |
| `--harness ... --block abc` | mensagem de erro em português, código 2 |
| `SampleDrum.Offline --kit <pasta> --block 480 --seed 7` | OK, código 0 |
| `dotnet publish ... -r win-x64 --self-contained` + exe publicado: `--harness --realtime`, `--selftest`, `--list-drivers` | OK, OK, "nenhum driver ASIO instalado" |
| exe publicado com `--simulation` | abre e fecha sem erro (ainda é a janela v1, ver abaixo) |

Resumo por peça do cenário sintético (semente 1, bloco 256; igual no offline e no tempo real):

| Peça | Entrada | Saída | Esperados | Silêncios esperados | Detectados | ok | faltando | extra | saída-errada | erro médio (ms) | erro máx (ms) | Resultado |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Bumbo | USB 3 | USB 1 | 9 | 1 | 9 | 10 | 0 | 0 | 0 | 0.02 | 0.17 | OK |
| Caixa | USB 4 | USB 2 | 9 | 1 | 9 | 10 | 0 | 0 | 0 | 0.01 | 0.04 | OK |
| Tom | USB 5 | USB 3 | 8 | 9 | 8 | 17 | 0 | 0 | 0 | 0.01 | 0.02 | OK |
| Surdo | USB 6 | USB 4 | 7 | 10 | 7 | 17 | 0 | 0 | 0 | 0.31 | 0.60 | OK |

As verificações do `--selftest` (as mesmas de `HarnessTests`): cada entrada dispara a peça e a saída esperadas;
2, 3 e 4 peças disparam juntas; desativar uma peça silencia só o retorno dela; o teste manual de cada peça chega só
na saída dela (mesmo com o kit desativado); batidas na fronteira de bloco detectadas na mesma amostra nos 4 tamanhos
de bloco; vazamento não dispara a peça errada; troca de kit (`ApplyKit`) no meio da execução (a voz antiga termina
com o kit antigo, as novas usam o novo) offline e com o host em tempo real rodando; migração do `settings.json` v1
(numa pasta temporária, não no seu %AppData%) mantendo o bumbo e a rota dele no perfil Igreja; conversão dos formatos
nativos do ASIO.

Para provar que o relatório reprova quando deve, durante o desenvolvimento foram feitas rodadas negativas (fora do
repositório): limiar −24 em todas as peças e também os padrões do Core sem a exceção do surdo (o vazamento do bumbo
vira **extra** no surdo), saídas 1 e 2 trocadas (**saída-errada** em bumbo e caixa) e surdo desligado (todas as
batidas do surdo **faltando**). O teste automático `HarnessTests.EvaluatorFlagsMissingExtraAndWrongOutput` mantém
essa garantia: remove as detecções da caixa, inventa um disparo do tom e manda o áudio do bumbo para a USB 2, e exige
**faltando**, **extra** e **saída-errada**.

## Limitações

* **Precisa da igreja / do hardware:** abrir o X-USB por ASIO, formatos nativos reais, latência real ida e volta,
  convivência com o OBS, sinais reais dos microfones e o vazamento real entre eles (calibrar os limiares por peça).
* O ritmo "tempo real" do host de simulação é feito por relógio (`Stopwatch`) e `Thread.Sleep(1)`: os blocos são
  processados no tempo certo em média, mas em rajadas do tamanho da resolução do timer do Windows. Serve para ouvir e
  exercitar a interface; não é uma medida de latência.
* O motor v2 não tem "gate" entre canais: vazamento acima do limiar de uma peça dispara essa peça (ver nota do surdo).
* `--simulation` só liga a opção; a janela atual ainda é a v1 (só bumbo). A janela v2 de 4 peças (do worker
  multicanal) é quem usa `StartupOptions` para abrir já no perfil Simulação com o kit sintético.
