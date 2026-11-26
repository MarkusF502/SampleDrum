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

No PowerShell, o `SampleDrum.App.exe` é um programa de janela (WinExe) e o PowerShell não espera por ele: para ver
a saída e o código de saída, **sempre** termine o comando com um pipe:

```powershell
.\SampleDrum.App.exe --harness C:\temp\sd-teste | Out-Host; $LASTEXITCODE

# para guardar a saída num arquivo
.\SampleDrum.App.exe --selftest | Out-File selftest.txt; Get-Content selftest.txt; $LASTEXITCODE
```

**Não use `> arquivo`** com o `SampleDrum.App.exe` (por exemplo `--selftest > selftest.txt`): o PowerShell não espera
o programa terminar e o arquivo fica vazio (verificado no Windows PowerShell 5.1). No Prompt de Comando use
`start /wait SampleDrum.App.exe --selftest` e `echo %ERRORLEVEL%`. O `SampleDrum.Offline` é um programa de console
comum e não tem esse problema.

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

Status no relatório: **ok** (disparou onde devia e o sample começou só na saída certa; ou não disparou onde não
devia), **faltando** (devia disparar e não disparou), **extra** (disparou sem dever: vazamento, segunda batida
dentro do intervalo mínimo ou disparo inesperado; ou áudio que começa numa saída sem nenhum disparo que o explique),
**saída-errada** (disparou, mas o sample não começou na saída USB daquela peça, ou começou também em outra saída) e
**limitação conhecida** (caso documentado que não reprova a rodada, por exemplo o golpe do surdo abaixo do limiar;
aparece numa seção própria do `relatorio.md`). Os eventos e as linhas saem em ordem de amostra (o motor entrega os
eventos de um mesmo bloco agrupados por peça; o harness reordena).

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
3. O relatório já faz essa conferência automaticamente, por **reconstrução** e não por "tem algum som ali":
   * cada disparo vira uma voz esperada (ganho do evento × sample da peça, a partir da amostra em que começou a
     tocar, com a mesma regra de polifonia/fade do motor); a saída esperada de cada USB é a soma das vozes das
     peças roteadas para ela;
   * para cada disparo, em cada saída, tira-se do áudio todas as **outras** vozes esperadas e mede-se a correlação
     do que sobra com esta voz nos primeiros 3 ms: na saída da peça tem de dar ≈ 1 (um começo **novo** deste sample;
     uma voz anterior ainda soando não conta) e em qualquer outra saída tem de dar ≈ 0. Se não, **saída-errada**
     (com a saída onde ele apareceu, quando há);
   * por fim procura-se, em cada saída, áudio que **começa** (subida de +9 dB acima de −60 dBFS) e que não é
     explicado por nenhum disparo: vira **extra** ("áudio começando na saída USB n sem disparo que o explique").
   Testes que garantem isso (`HarnessTests`): retorno do bumbo copiado também na USB 2 → todas as linhas do bumbo
   **saída-errada** (USB 2); tom constante de −40 dBFS na USB 1 sem as vozes do bumbo → bumbo **saída-errada** +
   **extra**; segunda voz de uma dupla removida (a primeira ainda soando) → **saída-errada** naquela batida;
   rajada solta numa saída certa → **extra**.
4. Os arquivos `retorno-<peça>.wav` são os mesmos sinais separados por peça (útil para escutar uma peça de cada vez).

## O cenário sintético

Quatro trilhas de microfone a 48 kHz, 16,5 s, determinísticas pela semente (ruído de fundo −60 dBFS em cada trilha):

| Trecho | O que testa |
|---|---|
| 0,5–2,0 s | uma batida forte isolada por peça |
| 2,5–4,0 s | uma batida suave isolada por peça (≈ 8–12 dB acima do limiar) |
| 4,8–6,0 s | uma batida por peça começando 1–7 amostras antes de uma fronteira de bloco comum a 32/64/128/256/480 (múltiplos de 3840 amostras) |
| 6,6 / 7,2 / 7,8 s | batidas simultâneas na mesma amostra: 2 peças, 3 peças, 4 peças |
| 8,5–10,7 s | duplas por peça com 120 ms (fora do intervalo mínimo): as duas devem disparar |
| 11,3–13,4 s | duplas por peça com 20 ms (dentro do intervalo mínimo): só a primeira deve disparar |
| 14,0 / 14,5 / 15,0 s | surdo suave a −10, −13 e −15 dBFS (acima do limiar de −16): devem disparar |
| 15,5 s | surdo suave a −18 dBFS (abaixo do limiar): **não** dispara — aparece como **limitação conhecida** |
| o tempo todo | vazamento controlado: bumbo → microfone do surdo (−18 dB, +1 ms) e caixa → microfone do tom (−15 dB, +0,5 ms), que **não** devem disparar surdo/tom |

Cada peça tem um timbre de microfone e um sample sintético diferentes (bumbo com varredura 120→50 Hz, caixa com
ruído + tom, tom ~190→140 Hz, surdo ~120→85 Hz), para os retornos serem fáceis de distinguir de ouvido.

Detecção usada no cenário: **exatamente** os padrões por peça do Core (`KitDefaults`: Bumbo −24, Caixa −20,
Tom −20, Surdo −16 dBFS, com os filtros e tempos de cada peça), sem exceções, para o cenário e os padrões nunca
divergirem em silêncio. O padrão do Surdo era −22 dBFS e foi elevado para −16: com −22, uma batida forte de bumbo
(−3 dBFS) vazando a −18 dB chega a ≈ −21 dBFS no microfone do surdo e o dispara (1 disparo extra por rodada,
medido com as sementes 1, 2, 3 e 7). Com −16 esse vazamento fica 5 dB abaixo e os golpes suaves do surdo (−6 dBFS)
10 dB acima. O vazamento caixa → tom (−15 dB) não dispara o Tom nem com −22, então Tom e Caixa ficaram como estavam.
Isto é uma **nota de calibração para o ensaio**: o motor não tem "gate" entre canais na v2, então o limiar de cada
tom precisa ficar acima do vazamento mais forte que o microfone dele recebe; os valores reais saem das gravações da
igreja. Com `--mics` (seus WAVs) o harness também usa os padrões do Core.

**O preço do −16 no surdo (medido no harness, sementes 1, 2, 3, 4, 5, 7 e 11, blocos 64/128/256/480):**

* margem de 5 dB sobre o vazamento sintético do bumbo (≈ −21 dBFS no microfone do surdo): não dispara;
* golpes suaves do surdo **a −16 dBFS ou abaixo não disparam** (o de −18 dBFS do cenário fica como limitação
  conhecida); acima disso disparam (−10, −13 e −15 dBFS);
* golpes até ~3 dB acima do limiar só cruzam o limiar na primeira crista da onda: −13 dBFS é detectado ≈ 4,5 ms
  depois do início e −15 dBFS ≈ 5,0 ms depois (−10 dBFS ≈ 0,8 ms). Por isso essas duas expectativas usam tolerância
  de 8 ms (as demais 5 ms); o erro aparece no relatório (é ele que leva o "erro máx." do surdo a ≈ 5 ms);
* o valor final sai das gravações da igreja: se o vazamento real do bumbo for mais fraco, dá para baixar o limiar do
  surdo e recuperar os golpes suaves; se for mais forte, subir.

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

## Verificações executadas (branch `ao/asio-app/root`, Release, neste PC sem X32 e sem ASIO)

Última rodada em 26/11/2026, depois das correções da revisão (atribuição de saída por reconstrução, golpes
suaves do surdo, ordem dos eventos); as linhas marcadas "25/11" não foram repetidas nesta rodada.

| Verificação | Resultado |
|---|---|
| `dotnet build SampleDrum.sln -c Release` | 0 avisos, 0 erros |
| `dotnet test SampleDrum.sln -c Release` (3 vezes, a última depois das correções de mensagens) | 263 testes no total. App (Windows): 7/7. Core: 252/256 — os 21 `HarnessTests` passam; falham as 4 sementes de `KitEngineTests.DefaultKit_PassesSyntheticScenario_KickBleedDoesNotFireFloorTom` (arquivo do worker multicanal), que confere todo o gabarito com tolerância fixa de ±2 ms e ainda não usa a tolerância de 8 ms dos golpes do surdo perto do limiar (correção de 1 linha pedida) |
| `SampleDrum.App.exe --selftest` | 11/11 OK em ~8–9 s, código 0 |
| Windows PowerShell 5.1: `--selftest \| Out-File x.txt; Get-Content x.txt; $LASTEXITCODE` e `--harness --help \| Out-Host` | saída completa no arquivo/console, código 0 |
| `SampleDrum.Offline --mic C:\nao\existe.wav` e `--kit <pasta> --mics a.wav b.wav c.wav d.wav` (arquivos inexistentes) | "Erro: arquivo não encontrado: …" em português; códigos 1 e 2 |
| `SampleDrum.Offline --generate-synthetic <pasta>` | exemplo impresso com `dotnet run --project src/SampleDrum.Offline -c Release -- …` |
| `--harness <pasta>` com bloco 64, 128, 256 e 480 | OK nos quatro, código 0 |
| `--harness <pasta> --realtime` (tempo real 1×) | offline OK e tempo real OK, disparos idênticos ao offline |
| cenário em 7 sementes (1, 2, 3, 4, 5, 7, 11) × blocos 64/128/256/480, avaliador novo | 28/28 OK (nenhum alarme falso da nova verificação de saída) |
| `--harness <pasta> --play` (25/11) | OK; saída padrão aberta, 0 underruns, 0 descartes (o áudio não foi escutado por quem rodou) |
| (25/11) `--harness --mics a b c d` e `--mics4 mics-4ch.wav --block 64` | rodaram até o fim (sem gabarito), código 0 |
| (25/11) `--harness ... --block abc` | mensagem de erro em português, código 2 |
| (25/11) `SampleDrum.Offline --kit <pasta> --block 480 --seed 7` | OK, código 0 |
| (25/11) `dotnet publish ... -r win-x64 --self-contained` + exe publicado: `--harness --realtime`, `--selftest`, `--list-drivers` | OK, OK, "nenhum driver ASIO instalado" |
| (25/11) exe publicado com `--simulation` | abre e fecha sem erro (nesta data a janela ainda não usava a opção; ligada depois pelo worker multicanal, ver abaixo) |

Resumo por peça do cenário sintético (semente 1, bloco 256; igual no offline e no tempo real):

| Peça | Entrada | Saída | Esperados | Silêncios esperados | Detectados | ok | faltando | extra | saída-errada | limitação conhecida | erro médio (ms) | erro máx (ms) | Resultado |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Bumbo | USB 3 | USB 1 | 9 | 1 | 9 | 10 | 0 | 0 | 0 | 0 | 0.02 | 0.17 | OK |
| Caixa | USB 4 | USB 2 | 9 | 1 | 9 | 10 | 0 | 0 | 0 | 0 | 0.01 | 0.04 | OK |
| Tom | USB 5 | USB 3 | 8 | 9 | 8 | 17 | 0 | 0 | 0 | 0 | 0.01 | 0.02 | OK |
| Surdo | USB 6 | USB 4 | 10 | 11 | 10 | 20 | 0 | 0 | 0 | 1 | 1.24 | 4.98 | OK |

(O "erro máx." de ≈ 5 ms do surdo é o golpe de −15 dBFS, 1 dB acima do limiar: ver "O preço do −16 no surdo".)

As verificações do `--selftest` (as mesmas de `HarnessTests`): cada entrada dispara a peça e a saída esperadas;
2, 3 e 4 peças disparam juntas; desativar uma peça silencia só o retorno dela; o teste manual de cada peça chega só
na saída dela (mesmo com o kit desativado); batidas na fronteira de bloco detectadas na mesma amostra nos 4 tamanhos
de bloco; vazamento não dispara a peça errada; troca de kit (`ApplyKit`) no meio da execução (a voz antiga termina
com o kit antigo, as novas usam o novo) offline e com o host em tempo real rodando; migração do `settings.json` v1
(numa pasta temporária, não no seu %AppData%) mantendo o bumbo e a rota dele no perfil Igreja; conversão dos formatos
nativos do ASIO.

Para provar que o relatório reprova quando deve, durante o desenvolvimento foram feitas rodadas negativas (fora do
repositório): limiar −24 em todas as peças e também o antigo padrão −22 dBFS do surdo (o vazamento do bumbo
vira **extra** no surdo; por isso o padrão passou a −16), saídas 1 e 2 trocadas (**saída-errada** em bumbo e caixa) e surdo desligado (todas as
batidas do surdo **faltando**). O teste automático `HarnessTests.EvaluatorFlagsMissingExtraAndWrongOutput` mantém
essa garantia: remove as detecções da caixa, inventa um disparo do tom e manda o áudio do bumbo para a USB 2, e exige
**faltando**, **extra** e **saída-errada**. A revisão independente mostrou que a verificação de saída antiga ("tem
algum som acima de −60 dBFS nos 3 ms seguintes") aprovava o bumbo copiado também na USB 2 e um tom constante no lugar
das vozes; os testes `ProbeKickReturnCopiedOntoUsb2Fails`, `ProbeConstantToneInsteadOfVoicesFails`,
`RingingEarlierVoiceDoesNotCountAsTheNewOnset` e `StrayAudioOnACorrectOutputIsExtra` reproduzem esses casos e agora
exigem a reprovação.

## Limitações

* **Precisa da igreja / do hardware:** abrir o X-USB por ASIO, formatos nativos reais, latência real ida e volta,
  convivência com o OBS, sinais reais dos microfones e o vazamento real entre eles (calibrar os limiares por peça).
* O ritmo "tempo real" do host de simulação é feito por relógio (`Stopwatch`) e `Thread.Sleep(1)`: os blocos são
  processados no tempo certo em média, mas em rajadas do tamanho da resolução do timer do Windows. Serve para ouvir e
  exercitar a interface; não é uma medida de latência.
* O motor v2 não tem "gate" entre canais: vazamento acima do limiar de uma peça dispara essa peça (ver nota do surdo).
* (Resolvido pelo worker multicanal.) A janela v2 lê `StartupOptions` (`--simulation` abre no perfil Simulação com
  o kit sintético) e a "Simulação (sem ASIO)" da janela usa `new FakeAudioHost(kit, roteamento, opções)` com as
  quatro peças, fonte, bloco, repetição e prévia escolhidos no painel "Simulação (sem ASIO)". O construtor de
  compatibilidade v1 do `FakeAudioHost` foi removido.
