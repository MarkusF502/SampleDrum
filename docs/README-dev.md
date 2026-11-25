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
| 5 | Velocity | −30/−18/−6 dBFS crescentes; ganho dentro de `[MinGainDb, 0 dB]`; satura em 1 acima de limiar + `VelocityRangeDb`; monotônica em passos finos; `FixedVelocity` → 0 dB |
| 6 | Ressonância e vazamento | ringing de 50 Hz contínuo abaixo do limiar: nada; caixa 20 dB abaixo do bumbo: nada; vazamento **acima** do limiar dispara sem filtro e é rejeitado com `LowPassHz = 150` (bumbos ainda detectados) |
| 7 | Clique isolado de 1-2 amostras | rejeitado pelo detect time; aceito com `DetectTimeMs = 0`; não atrapalha um golpe real 10 ms depois |
| 8 | Latência | o sample começa em onset + `max(DetectTimeMs, ScanMs)`, ≤ detect time + 1 amostra com os padrões; com tudo em 0 começa na amostra do cruzamento, no mesmo bloco |
| 9 | Sequência rápida além de `MaxVoices` | 8 golpes a 45 ms com 2 vozes: sem degrau/clique (roubo com fade) e **zero alocação** |
| — | Motor | desabilitado = silêncio exato; teste manual toca mesmo desabilitado; ajustes aplicados no bloco seguinte sem disparo espúrio; fila de eventos; medidor de pico; `Process` não aloca |
| — | ChannelMap | USB 3 → índice 2, USB 1 → índice 0, fora da faixa é rejeitado |
| — | WAV | ida e volta float/PCM24/PCM16, estéreo → mono, chunks extras, reamostragem 44,1k/96k → 48k |

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
   ... --scan-ms 2                 # velocity pelo pico real do golpe, +1,5 ms de latência
   ... --lpf-hz 150                # rejeitar vazamento de caixa/tons
   ... --retrigger-db 0            # comportamento parecido com o LSP (sem redisparo no sustain)
   ```

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
velocity pelo pico da janela e índice de amostra absoluto nos eventos.

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
  isolado → volta a OFF sem mexer na trava. No fim da janela dispara, com a velocity do pico da janela → ON.
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
| `ScanMs` | 0 (= só o detect time) | menor latência (0,5 ms no total). Ver compromisso abaixo |
| `EnvelopeReleaseMs` | 20 ms | > 1 período de 50 Hz |
| `HighPassHz` | 40 Hz | tira ronco/vibração de palco sem tocar na fundamental do bumbo |
| `LowPassHz` | desligado | ligar em 150-250 Hz só se o vazamento de caixa/tons disparar (adiciona ~1 ms de atraso) |
| `VelocityRangeDb` | 24 dB | pesquisa; calibrar ≈ (pico medido do golpe forte − limiar) |
| `MinGainDb` | −20 dB | pesquisa; golpe no limiar toca a −20 dB, golpe forte a 0 dB |
| `MaxVoices` / `StealFadeMs` | 4 / 3 ms | pesquisa; roubo da mais antiga com fade |

### Compromisso latência × velocity (janela de varredura)

O cruzamento do limiar acontece no início do ataque. Com `ScanMs = 0` a velocity é medida nos 0,5 ms do
detect time, ou seja, na **subida** do golpe e não no pico. Ela continua **monotônica** com a força do
golpe (golpes com o mesmo formato sobem proporcionalmente), mas lê alguns dB abaixo do pico real: no bumbo
sintético, um golpe de −3 dBFS mede ≈ −9 dBFS. Por isso:

- calibre `VelocityRangeDb` pelo **pico medido** que o modo offline imprime (golpe forte medido − limiar);
- ou use `ScanMs = 1-2 ms` para medir o pico real, pagando exatamente esse atraso a mais;
- `DetectTimeMs = 0` e `ScanMs = 0` disparam na amostra do cruzamento (latência zero adicional), mas
  aceitam cliques isolados e a velocity fica perto do limiar (use `FixedVelocity`).

A latência total ao vivo = latência de entrada ASIO + `max(DetectTimeMs, ScanMs)` + latência de saída ASIO.
É fixa e determinística, então dá para compensar com delay no canal do microfone na mesa, se preciso.

### Limites conhecidos do redisparo

- O 2º golpe precisa subir `RetriggerRiseDb` (9 dB) acima do sustain já decaído do 1º. Dois golpes iguais
  sobre um sustain que decaiu só 3-6 dB (muito próximos ou sustain muito longo) ainda viram um evento só.
  Os testes usam sustains que decaíram ~12 dB no momento do 2º golpe.
- **Não use `EnvelopeReleaseMs` curto com o redisparo ligado**: com 3 ms o envelope despenca entre os
  picos de um sustain de 50 Hz e cada ciclo parece um golpe novo (há um teste documentando isso).
  Mantenha o release ≥ 1 período da fundamental (20 ms cobre 50 Hz).

## Contrato com o app (thread de áudio)

- `TriggerEngine.Process(input, output)`: thread de áudio; não aloca, não trava, não faz log/IO. `output`
  vem zerado pelo chamador e o motor **soma** nele. Um disparo dentro do bloco começa no offset exato.
- `UpdateSettings` / `SetSample`: fora da thread de áudio; troca atômica de referência aplicada no início
  do próximo bloco. Coeficientes são calculados em `UpdateSettings`, não no áudio. O array passado em
  `SetSample` passa a pertencer ao motor (não modifique depois). Use `WavFile.ReadMonoAtRate` para carregar.
- `RequestTest(gain)`: qualquer thread; toca no início do próximo bloco, mesmo com `Enabled = false`.
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
