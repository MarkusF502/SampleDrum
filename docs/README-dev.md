# SampleDrum — guia do desenvolvedor (Core + Offline)

Este documento cobre o motor de detecção/disparo (`src/SampleDrum.Core`), o modo offline
(`src/SampleDrum.Offline`) e os testes (`tests/SampleDrum.Core.Tests`). A interface Windows/ASIO
(`src/SampleDrum.App`) tem documentação própria. A pesquisa que embasa o detector está em
[`docs/research/lsp-trigger.md`](research/lsp-trigger.md).

## Pré-requisitos

- .NET SDK 10 (testado com 10.0.401). Verifique com `dotnet --list-sdks`.
- `SampleDrum.Core` e `SampleDrum.Offline` são `net10.0` **sem pacotes externos**: compilam e rodam em
  Windows, Linux e WSL. Os testes usam xUnit (baixado do NuGet no primeiro `restore`).

## Compilar

Na raiz do repositório:

```powershell
dotnet build SampleDrum.sln
```

## Rodar os testes

```powershell
dotnet test SampleDrum.sln
```

Para ver o nome de cada teste: `dotnet test SampleDrum.sln --logger "console;verbosity=detailed"`.
Para rodar só um grupo: `dotnet test --filter "FullyQualifiedName~DetectorTests"`.

Tolerância de tempo usada nos testes: **±1 ms = 48 amostras a 48 kHz** entre o início real do golpe e o
onset reportado (±2 ms quando o passa-baixas está ligado, por causa do atraso de grupo do filtro).

| # da pesquisa | Teste | O que garante |
|---|---|---|
| 1 | Silêncio, ruído a −60 e −70 dBFS | 0 eventos, saída **exatamente** zero, envelope não deriva |
| 2 | Golpe único com sustain longo (inclui seno de 55 Hz/400 ms com clique) | exatamente 1 evento, também com `RearmTimeMs = 0` (cruzamentos por zero não rearmam) |
| 3 | Dois golpes próximos | 20 ms (< 35 ms de trava) → 1 evento; 40/60/100 ms com o sustain do 1º **acima do release** → 2 eventos via redisparo (e só 1 com o redisparo desligado); golpes curtos acima/abaixo do intervalo mínimo |
| 3 | Golpe suprimido com sustain longo | não dispara "atrasado" quando a trava termina |
| 4 | Fronteira de bloco | blocos 1/7/32/61/64/127/128/256/480/512/1000/1024: eventos **e saída idênticos** ao buffer inteiro, golpe detectado 1 vez na amostra certa |
| 4 | Faixa sintética completa | os 10 golpes casados, para vários tamanhos de bloco |
| 5 | Velocity | −30/−18/−6 dBFS crescentes; ganho dentro de `[MinGainDb, 0 dB]`; satura em 1 acima de limiar + `VelocityRangeDb`; monotônica em passos finos; `FixedVelocity` → 0 dB; segue o mapeamento em dB do nível real (−0,12/+0,05) para bumbo com e sem clique, com e sem passa-baixas; bumbo sem clique de 55 Hz: −0,25 com 2 ms; golpe suave sem clique (−18 dBFS) nunca fica abaixo do nível que disparou; ronco de 5 Hz a −14 dBFS não mexe na velocity (variação < 0,1 entre fases) |
| 6 | Ressonância e vazamento | ringing de 50 Hz contínuo abaixo do limiar: nada; caixa 20 dB abaixo do bumbo: nada; vazamento **acima** do limiar dispara sem filtro e é rejeitado com `LowPassHz = 150` (bumbos ainda detectados) |
| 7 | Clique isolado de 1-2 amostras | rejeitado pelo detect time; aceito com `DetectTimeMs = 0`; não atrapalha um golpe real 10 ms depois |
| 8 | Latência | o sample começa em onset + `max(DetectTimeMs, ScanMs)` (≤ janela + 1 amostra; 2 ms com os padrões); com tudo em 0 começa na amostra do cruzamento, no mesmo bloco |
| 9 | Sequência rápida além de `MaxVoices` | 8 golpes a 45 ms com 2 vozes: sem degrau/clique (roubo com fade) e **zero alocação** |
| — | Motor | desabilitado = silêncio exato; teste manual toca mesmo desabilitado; ajustes aplicados no bloco seguinte sem disparo espúrio (inclusive no meio do DETECT: a janela é congelada no cruzamento); fila de eventos; medidor de pico; `Process` não aloca; `Reset()` impede que uma voz cortada pelo "Parar" volte no próximo "Iniciar"; estados dos filtros chegam a 0 exato no silêncio (sem denormais); NaN/infinito na entrada ou no sample não chegam à saída; pool cheio nunca corta uma voz que está tocando |
| — | ChannelMap | USB 3 → índice 2, USB 1 → índice 0, fora da faixa é rejeitado |
| — | WAV | ida e volta float/PCM24/PCM16, estéreo → mono, seleção de canal, chunks extras, WAVE_FORMAT_EXTENSIBLE (PCM24 e float), chunk `fmt ` curto rejeitado, NaN/infinito trocados por 0 e contados, reamostragem 44,1k/96k → 48k |

## Modo offline com dados sintéticos

1. Gerar a faixa sintética (mic de bumbo + sample + onsets esperados):

   ```powershell
   dotnet run --project src/SampleDrum.Offline -- --generate-synthetic synthetic-out
   ```

   Gera em `synthetic-out/` (pasta ignorada pelo git):
   - `mic.wav` — 9 s, 48 kHz, mono, PCM24: ruído de fundo a −60 dBFS; golpe forte (−3 dB), suave (−12 dB),
     um com sustain grave longo (constante de decaimento de 300 ms), dois golpes a 100 ms um do outro,
     um golpe que começa 3 amostras antes de uma fronteira de bloco comum a 32/64/128/256/480/512/1024, e uma
     escada de intensidade (−18/−13/−8/−3 dBFS). O golpe mais fraco fica 12 dB acima do limiar padrão.
   - `kick.wav` — sample sintético de bumbo (~400 ms).
   - `expected.csv` — `sample_index,time_ms,level_db,sustain_ms,label` de cada golpe.

2. Processar e comparar:

   ```powershell
   dotnet run --project src/SampleDrum.Offline -- --mic synthetic-out/mic.wav --sample synthetic-out/kick.wav --out synthetic-out/triggers.wav --expected synthetic-out/expected.csv
   ```

   Saída: número de eventos e, para cada um, tempo (ms), índice de amostra, pico medido (dBFS), velocity,
   ganho aplicado, a amostra onde o sample começou e se foi um redisparo no sustain; depois a comparação
   esperado × detectado com o erro por evento. O WAV `triggers.wav` contém **só** o sample disparado
   (48 kHz mono; `--format float|pcm24|pcm16`).

   Código de saída: `0` ok, `1` erro de argumento/arquivo, `2` a comparação teve golpes perdidos ou extras.

3. Experimentos úteis:

   ```powershell
   ... --block 480                 # outro tamanho de bloco (o resultado deve ser idêntico)
   ... --min-interval-ms 150       # trava maior que o espaçamento dos golpes próximos: o 2º sai PERDIDO
   ... --detect-ms 0               # sem debounce (latência mínima, cliques isolados passam)
   ... --scan-ms 3                 # bumbo sem clique/muito grave: velocity mais fiel, +1 ms de latência
   ... --scan-ms 0                 # menor latência (0,5 ms), velocity subestimada
   ... --lpf-hz 150                # rejeitar vazamento de caixa/tons
   ... --retrigger-db 0            # comportamento parecido com o LSP (sem redisparo no sustain)
   ... --channel 2                 # usa só a faixa 2 de um WAV multicanal (padrão: média das faixas)
   ... --events-out onsets.csv     # grava os onsets detectados; o arquivo serve de --expected para outra faixa
   ```

   Medir atraso entre duas faixas (ex.: CH 21 e Aux 1 gravados juntos): rode a faixa 1 com `--events-out`
   e depois a faixa 2 com `--expected` desse arquivo e `--tolerance-ms 50`; o erro por evento é o atraso.

Todas as opções: `dotnet run --project src/SampleDrum.Offline -- --help`.
Números usam ponto decimal (`--threshold-db -27.5`). `--threshold-db` move o release junto (mantém −6 dB),
a menos que `--rearm-db` seja informado.

Arquivos que não estão em 48 kHz são reamostrados offline (sinc com janela) e convertidos para mono,
com um aviso no console. Os índices de amostra reportados são sempre absolutos e em 48 kHz.

## Como o detector funciona

Reimplementação própria a partir do comportamento descrito em `docs/research/lsp-trigger.md`
(LSP Trigger, de lsp-plug.in). O LSP é **LGPL-3**: nenhum código dele foi copiado ou traduzido; ele é só
referência de comportamento. Diferenças principais em relação ao LSP: envelope *peak-hold* em vez de RMS,
**intervalo mínimo absoluto** e **redisparo por subida** durante o sustain (o LSP não tem nenhum dos dois),
velocity por um caminho próprio (passa-altas suave de 20 Hz) e índice de amostra absoluto nos eventos.

Caminho do sinal (mesmo código ao vivo e offline, amostra por amostra, estado guardado entre blocos):

```
entrada → passa-altas 40 Hz (12 dB/oit) → [passa-baixas opcional] → |y| →
envelope peak-hold (ataque instantâneo, release exponencial de 20 ms)
```

O envelope — nunca |y| cru — decide o release: os cruzamentos por zero de um bumbo de 50-60 Hz
(a cada ~8-10 ms) não parecem "fim do golpe".

Máquina de estados `OFF → DETECT → ON → RELEASE → OFF`:

- **OFF** (armado): uma amostra com |y| ≥ `ThresholdDb` é o *onset*. Se estiver a menos de `MinIntervalMs`
  do último onset aceito, é **suprimida** e o detector vai direto para ON (o sustain desse golpe não dispara
  depois). Senão, vai para DETECT.
- **DETECT**: janela de `max(DetectTimeMs, ScanMs)` amostras a partir do onset. Depois de `DetectTimeMs`,
  a média quadrática de |y| na janela precisa atingir o limiar (*debounce*); se não atingir, era um pico
  isolado → volta a OFF sem mexer na trava. No fim da janela dispara, com a velocity do pico do caminho de
  velocity na janela → ON. Os tamanhos da janela são congelados no cruzamento (uma troca de ajustes no
  meio do DETECT não pula o debounce).
- **ON → RELEASE** quando o envelope cai a `RearmDb`; **RELEASE → ON** se subir de novo;
  **RELEASE → OFF** (rearmado) depois de `RearmTimeMs` abaixo dele.
- **Redisparo** (em ON/RELEASE): depois da trava, se o envelope subir `RetriggerRiseDb` acima do seu mínimo
  desde o fim da trava, e estiver acima do limiar, é um golpe novo → DETECT (marcado como redisparo).
- Trocar os ajustes nunca dispara sozinho: se o envelope já está acima do novo limiar, o detector vai
  para ON e rearma pelo caminho normal.

Velocity e ganho:

```
velocity = clamp((picoDb − ThresholdDb) / VelocityRangeDb, 0, 1)        (linear em dB)
ganhoDb  = (1 − velocity) · MinGainDb + OutputGainDb                    (FixedVelocity → só OutputGainDb)
```

### Padrões escolhidos (`TriggerSettings.Default`) e por quê

| Ajuste | Padrão | Por quê |
|---|---|---|
| `ThresholdDb` | −24 dBFS | sugestão da pesquisa; deve ficar 12-18 dB abaixo do golpe mais fraco e ≥ 10 dB acima do vazamento — calibrar com a gravação real |
| `DetectTimeMs` | 0,5 ms (24 amostras) | rejeita cliques de 1-2 amostras custando só 0,5 ms de latência |
| `RearmDb` | limiar − 6 dB (−30 dBFS) | histerese da pesquisa; 6 dB > ondulação do envelope de 20 ms num sustain de 50-55 Hz (~4 dB). Continua absoluto em dBFS por compatibilidade com o app; use `WithThreshold()` para mover o limiar mantendo o −6 dB |
| `RearmTimeMs` | 15 ms | pesquisa; > meio período da fundamental |
| `MinIntervalMs` | 35 ms | trava absoluta da pesquisa; abaixo das duplas mais rápidas reais com folga |
| `RetriggerRiseDb` | 9 dB | pesquisa; maior que a ondulação do envelope (~4 dB a 55 Hz, ~5,4 dB a 40 Hz) |
| `ScanMs` | 2 ms | medido: a partir de 2 ms o erro em bumbos com clique ou varredura de pitch fica no patamar de ≈ −2 dB do filtro de 20 Hz; 2 ms de atraso no total. Ver tabela abaixo |
| `EnvelopeReleaseMs` | 20 ms | > 1 período de 50 Hz |
| `HighPassHz` | 40 Hz | tira ronco/vibração de palco sem tocar na fundamental do bumbo |
| `LowPassHz` | desligado | ligar em 150-250 Hz só se o vazamento de caixa/tons disparar (adiciona ~1 ms de atraso) |
| `VelocityRangeDb` | 24 dB | pesquisa; calibrar ≈ (pico medido do golpe forte − limiar) |
| `MinGainDb` | −20 dB | pesquisa; golpe no limiar toca a −20 dB, golpe forte a 0 dB |
| `MaxVoices` / `StealFadeMs` | 4 / 3 ms | pesquisa; roubo da mais antiga com fade |

### Compromisso latência × velocity (janela de varredura)

O cruzamento do limiar acontece no início do ataque; o pico real do golpe chega depois. A velocity é o pico,
durante `max(DetectTimeMs, ScanMs)` a partir do cruzamento, de **max(|caminho de velocity|, |caminho de
detecção|)**:

- **caminho de velocity** = entrada → passa-altas fixo de **20 Hz** (2ª ordem). Tira ronco e DC, que com a
  entrada crua faziam um ronco de 5 Hz a −14 dBFS variar a velocity de um bumbo de −12 dBFS entre 0,26 e 0,67
  conforme a fase; com o filtro a variação cai para 0,04. O passa-altas de 40 Hz da detecção não serve aqui:
  ele encolhe o primeiro meio-ciclo de um bumbo grave em até ~7 dB.
- o **max** com o caminho de detecção garante que a velocity nunca fica abaixo do nível que disparou (um golpe
  suave e sem clique a −18 dBFS dava velocity 0,00; agora 0,17 para 0,25 esperado).
- vazamento de caixa que coincida com os 2 ms da janela ainda entra no caminho de velocity (o passa-baixas
  opcional vale só para a detecção); é raro e pouco relevante.

Erro do pico medido em relação ao nível real do golpe (dB), limiar −24 dBFS, faixa 24 dB, 48 kHz
(gerado com `SyntheticSignals`; "com clique" = ruído de baqueta + seno 120→50 Hz; "sem clique" = só o seno,
começando na fase 0):

| Bumbo (nível) | 0 ms | 1 ms | 2 ms | 3 ms | 5 ms |
|---|---|---|---|---|---|
| com clique, 0 / −6 / −12 dBFS | −6,1 | −4,1 | −1,9 | −1,9 | −1,9 |
| sem clique 120→50 Hz, 0 dBFS | −7,9 | −3,8 | −2,0 | −2,0 | −1,8 |
| sem clique 120→50 Hz, −12 dBFS | −5,2 | −2,7 | −2,0 | −2,0 | −1,0 |
| sem clique 55 Hz, 0 dBFS | −13,1 | −8,9 | −5,0 | −3,9 | −3,8 |
| sem clique 55 Hz, −12 dBFS | −7,9 | −5,9 | −4,0 | −3,8 | −3,8 |
| sem clique 55 Hz, −18 dBFS | −4,6 | −3,5 | −2,0 | −0,4 | +0,2 |
| sem clique 55 Hz, 0 dBFS, passa-baixas 150 Hz | −6,0 | −4,8 | −3,8 | −3,8 | −3,8 |

(1 dB de erro ≈ 0,04 de velocity com faixa de 24 dB.) A partir de 2 ms o erro de bumbos com clique ou
varredura de pitch para num patamar de ≈ −2 dB, que é o custo do filtro de 20 Hz no primeiro meio-ciclo: é
**constante**, então a velocity continua proporcional e a calibração de `VelocityRangeDb` pelo pico medido o
absorve. Por isso o padrão é **2 ms**; janelas maiores só ajudam bumbos muito graves e sem ataque (55 Hz:
−5,0 → −3,9 dB com 3 ms). Comparado no mesmo teste: passa-altas de 1ª ordem (10-20 Hz) deixava o ronco variar
a velocity em 0,16-0,19 e 2ª ordem em 15 Hz em 0,07, com patamar de −1,5 dB; ficou 2ª ordem em 20 Hz
(variação 0,04, patamar −2 dB), que rejeita melhor o ronco de palco de 10-20 Hz.
A janela aparece no app em **Avançado → Janela de velocidade (ms)**, com o atraso resultante ao lado.

- `ScanMs = 0`: só os 0,5 ms do detect time (menor latência), mas a velocity lê 6-13 dB abaixo.
- `DetectTimeMs = 0` e `ScanMs = 0`: dispara na amostra do cruzamento, aceita cliques isolados e a velocity
  fica perto do limiar (use `FixedVelocity`).

**Por que não "começar no detect time e só subir o ganho durante a janela"** (velocity sem a latência da
janela): no detect time o pico parcial ainda está 6-13 dB abaixo do real (coluna 0 ms acima). Com
`MinGainDb = −20`, o **ataque do sample** (os primeiros ~1,5 ms, justamente a parte mais importante do bumbo)
tocaria 5-11 dB baixo e subiria em rampa até o ganho certo. Não gera clique, mas amacia o ataque de forma
audível e diferente a cada golpe, então não foi adotado. Se 1,5 ms a mais for problema no ensaio, reduza
`ScanMs` e aceite velocity menos fiel (ou use `FixedVelocity`).

A latência total ao vivo = latência de entrada ASIO + `max(DetectTimeMs, ScanMs)` + latência de saída ASIO.
É fixa e determinística, então dá para compensar com delay no canal do microfone na mesa, se preciso.

### Limites conhecidos do redisparo

- O 2º golpe precisa subir `RetriggerRiseDb` (9 dB) acima do sustain já decaído do 1º. Dois golpes iguais
  sobre um sustain que decaiu só 3-6 dB (muito próximos ou sustain muito longo) ainda viram um evento só.
  Os testes usam sustains que decaíram ~12 dB no momento do 2º golpe.
- **Não use `EnvelopeReleaseMs` curto com o redisparo ligado**: com 3 ms o envelope despenca entre os
  picos de um sustain de 50 Hz e cada ciclo parece um golpe novo (há um teste documentando isso).
  Mantenha o release ≥ 1 período da fundamental (20 ms cobre 50 Hz).

## v2: kit de quatro peças (`SampleDrum.Core.Kit`)

- `KitEngine` (`IMultiChannelProcessor`): quatro `TriggerPipeline` independentes (o mesmo núcleo do
  `TriggerEngine` da v1: detector, vozes, teste manual, medidor), sem estado mutável compartilhado; a única coisa
  comum é a fila de eventos (`KitEvent(Piece, TriggerEvent)`), escrita só pela thread de áudio. `Process` não
  aloca (teste com as quatro peças, testes manuais e trocas de kit).
- `ApplyKit(PreparedKit)`: uma troca de referência aplicada no início do bloco seguinte, para as quatro peças
  ao mesmo tempo (nenhum bloco mistura dois kits; há teste concorrente). Vozes já disparadas terminam com o
  sample antigo. Ganho/detecção opcionais do preset entram na mesma troca.
- `SampleLibrary`: lista WAVs (só cabeçalho) e prepara kits fora da thread de áudio; qualquer erro por peça →
  sem kit (o ativo continua). A troca de uma peça só usa `PreparePieceAsync` (kit atual sem ganhos/ajustes +
  a peça nova), para não desfazer ganhos/ajustes que o operador mudou depois do preset.
- `RoutingValidator` (por direção), `RoutingProfiles.Church/Simulation`, `ResolvedRouting` (peça → índices
  ASIO) e `AppConfig` v2 (migração da v1, presets, perfis editáveis).

### Padrões por peça (`KitDefaults`)

Derivados das regras da pesquisa (release do envelope maior que um período da fundamental; limiar ≥ ~10 dB acima
do vazamento; passa-altas abaixo da fundamental da peça). Todos precisam de calibração com gravação real.

| Peça | Limiar / rearme | Passa-altas | Release do envelope / rearme | Intervalo mínimo | Outros | Por quê |
|---|---|---|---|---|---|---|
| Bumbo | −24 / −30 dBFS | 40 Hz | 20 ms / 15 ms | 35 ms | — | padrões da v1 |
| Caixa | −20 / −26 dBFS | 100 Hz | 10 ms / 10 ms | 25 ms | faixa de velocidade 30 dB | fundamental 180-250 Hz (período 4-5,5 ms); muito vazamento de bumbo e chimbal; rulos e flams rápidos; ghost notes |
| Tom | −20 / −26 dBFS | 60 Hz | 15 ms / 15 ms | 30 ms | — | fundamental 100-150 Hz (7-10 ms); microfone ouve o kit inteiro e o tom toca pouco |
| Surdo | −16 / −22 dBFS | 50 Hz | 20 ms / 15 ms | 30 ms | — | fundamental 70-100 Hz (10-14 ms), perto do bumbo; o bumbo vaza forte no microfone do surdo: no cenário do harness um bumbo de −3 dBFS vazando a −18 dB (≈ −21 dBFS) disparava −22; −16 deixa 5 dB de folga sobre o vazamento e 10 dB sob os golpes suaves (−6 dBFS). O cenário usa estes padrões sem exceção. |

## API para o painel (Core, `SampleDrum.Core.Kit`)

Base para o painel no celular (o servidor e a interface do celular ficam para a próxima entrega; ver
`docs/research/painel-movel.md`). A janela do PC já usa o mesmo caminho.

- **`KitController`** — caminho único para mudar o kit (PC e, depois, o painel). Fila serializada: cada pedido vira
  uma `KitOperation` com `Id`, `Status` (`Queued`, `Preparing`, `Applied`, `Failed`, `Superseded`), erros por peça e
  `Completion` (Task). Operações: `SetPieceSample(peça, id)` (id **não confiável**, do painel), `SetPieceSamplePath`
  (caminho do seletor de arquivos do PC), `ApplyPreset` (kit inteiro de uma vez), `ApplyKit(PreparedKit, KitSettings?)`
  (kit já preparado, ex.: o sintético), `SetPieceSettings`, `SetPieceGain`, `SetPieceEnabled`, `SetKitEnabled`.
  - A preparação (ler/reamostrar WAV) roda fora da thread de áudio; só um kit completo chega ao motor
    (`KitEngine.ApplyKit`, uma troca no início do bloco). Preparação que falha não muda nada.
  - **`Version`** sobe 1 a cada operação aplicada; **`Snapshot`** traz por peça id/caminho do sample, ganho, ativa e
    ajustes, mais kit ativo e `ActivePresetId`. **`StateChanged`** é disparado na thread do controlador (nunca na de
    áudio): quem é UI precisa repassar para a própria thread.
  - **Último pedido vence, mas só sobre o que ele cobre**: kit inteiro (preset/kit preparado) cancela kits inteiros e
    trocas de sample pendentes; troca de sample de uma peça cancela só trocas pendentes **da mesma peça** (nunca um
    preset pendente, cujas outras peças se perderiam); ganho/ajustes/ativa/kit ligado cancelam só o mesmo tipo do mesmo
    alvo. Operações canceladas (`Superseded`) nunca tocam o motor, mesmo se já estavam preparando.
  - Guarda o estado das últimas 256 operações terminadas (`GetOperation`); o objeto `KitOperation` continua válido.
  - Quem usa o controlador não deve mudar o motor direto (exceto `RequestTest`, medidores e `Reset` com o áudio parado).
- **`SampleLibrary`**: `SampleInfo.Id` = caminho relativo à pasta de samples com `/`. **`TryResolveInLibrary(id)`**
  rejeita caminhos absolutos, letra de drive, UNC, `..`, `.`, segmentos vazios, streams alternativos (`:`), nomes de
  dispositivo (`NUL`, `COM1`...) e junções/links simbólicos abaixo da pasta. Entrada remota **sempre** passa por ele;
  `ResolvePath` (aceita caminhos absolutos) é só para o seletor de arquivos do PC.
- **`PieceLoadError.Code`**: `FileNotFound`, `UnsupportedFormat`, `TooLong`, `OutsideLibrary`, `IoError` (estável,
  para o painel), além da mensagem em português.
- **Presets**: `KitPreset.Id` = nome do arquivo sem `.json` (não é gravado no JSON; nomes repetidos não colidem);
  `PreparedKit.PresetId` vem do preset e fica `null` depois de trocar uma peça só (seleção personalizada).
- **`KitEngine`**: `ApplyKit` com taxa diferente falha na hora; `ActiveKit` = último kit enviado (também com o áudio
  parado); `Reset()` mantém (aplica) um kit pendente; `AppliedKitGeneration` mostra que a thread de áudio já pegou a
  troca. **`TryDequeueEvent` e `ReadAndResetInputPeak` têm um único consumidor**: o app lê e repassa para a janela e,
  no futuro, para o painel.
- **`AppConfig`**: `RemotePanel { Enabled = false, Port = 8750, AllowPhoneTest = false, PairedDevices[{ Name,
  TokenSha256Hex, PairedAtUtc, LastSeenUtc }] }` (só o esquema; guarda o hash do token, nunca o token; porta fora de
  1024-65535 volta a 8750) e `ActivePresetId`. `SamplePath` dentro da pasta de samples é gravado relativo (`kicks/k1.wav`)
  e volta absoluto ao carregar; fora dela fica absoluto. `Save` grava uma cópia (snapshot) num arquivo temporário e move
  por cima (atômico); `Save(caminho, KitStateSnapshot)` grava o estado do controlador sem mexer na configuração em memória.

## Contrato com o app (thread de áudio)

- `TriggerEngine.Process(input, output)`: thread de áudio; não aloca, não trava, não faz log/IO. `output`
  vem zerado pelo chamador e o motor **soma** nele. Um disparo dentro do bloco começa no offset exato.
- `UpdateSettings` / `SetSample`: fora da thread de áudio; troca atômica de referência aplicada no início
  do próximo bloco. Coeficientes são calculados em `UpdateSettings`, não no áudio. O array passado em
  `SetSample` passa a pertencer ao motor (não modifique depois). Use `WavFile.ReadMonoAtRate` para carregar.
- `RequestTest(gain)`: qualquer thread; toca no início do próximo bloco, mesmo com `Enabled = false`.
- `Reset()`: só com o áudio parado (o app chama antes de cada "Iniciar áudio"): limpa vozes, detector,
  filtros, trava e teste pendente; mantém sample, ajustes, `Enabled` e o contador absoluto de amostras.
- NaN/infinito: `SetSample` e `WavFile` trocam por 0 (contados em `LastSampleNonFiniteCount` /
  `WavData.NonFiniteSamples` e avisados na nota de conversão); o detector ignora NaN na entrada; no app, o
  conversor para o formato nativo transforma NaN em 0 e ±infinito em fundo de escala.
- `TryDequeueEvent`: um único consumidor (UI/log); fila SPSC sem lock de 1024 eventos; se encher, eventos
  são descartados e contados em `DroppedEvents` (o áudio nunca espera). `TriggerEvent.SampleIndex` é o
  onset (cruzamento) em índice absoluto; `PlaybackSampleIndex` é onde o sample começou; `IsRetrigger`
  indica redisparo no sustain.
- `ReadAndResetInputPeak`: pico linear desde a última leitura, para o medidor.
- Desabilitado: o detector continua rodando (estado consistente), mas não dispara; vozes automáticas que
  estavam tocando fazem fade de `StealFadeMs` e depois a saída é zero exato.
- `ChannelMap`: canais da mesa/USB são 1..32; índices ASIO são 0..31 (USB 3 → 2, USB 1 → 0).

## O que é validado onde

- **Compilado e testado offline** (sem hardware): todo o Core, o modo offline e os testes acima.
- **Precisa de hardware** (X32 + X-USB + ASIO) e da **gravação real** do bumbo: latência real,
  comportamento com o buffer do driver compartilhado com o OBS, calibração de `ThresholdDb`,
  `VelocityRangeDb`, `MinIntervalMs` e do redisparo com as duplas reais do baterista.
