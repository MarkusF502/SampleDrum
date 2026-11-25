# SampleDrum — guia do desenvolvedor (Core + Offline)

Este documento cobre o motor de detecção/disparo (`src/SampleDrum.Core`), o modo offline
(`src/SampleDrum.Offline`) e os testes (`tests/SampleDrum.Core.Tests`). A interface Windows/ASIO
(`src/SampleDrum.App`) tem documentação própria.

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

O que é coberto (tolerância de tempo: **±1 ms = 48 amostras a 48 kHz** entre o início real do golpe e o
onset reportado):

| Teste | O que garante |
|---|---|
| Silêncio / ruído de fundo | 0 eventos e saída **exatamente** zero |
| Golpe único com sustain longo | exatamente 1 evento (a histerese segura o sustain) |
| Dois golpes próximos | ambos detectados quando o espaçamento > intervalo mínimo; o 2º suprimido quando < |
| Golpe suprimido com sustain longo | não dispara "atrasado" quando o lockout termina |
| Golpe atravessando a fronteira de bloco | detectado 1 vez, na amostra certa, para blocos 1/7/32/61/64/127/128/256/480/512/1000, com eventos **e saída idênticos** ao processamento do buffer inteiro |
| Faixa sintética completa | todos os 10 golpes casados, para vários tamanhos de bloco |
| Velocity | monotônica com o nível do golpe; ganho também; `Dynamics = 0` → ganho fixo |
| Janela de varredura | `ScanMs = 0` inicia o sample na amostra do cruzamento, no mesmo bloco; com `ScanMs > 0` inicia em cruzamento + janela; o pico capturado é o da janela |
| Desabilitado | saída exatamente zero e sem eventos; ao desabilitar, vozes em andamento fazem fade curto |
| Teste manual | toca mesmo desabilitado, no início do próximo bloco, uma única vez |
| Roubo de voz | máximo de vozes respeitado, a mais antiga sai com fade (sem degrau/clique) |
| Ajustes | aplicados no início do bloco seguinte; baixar o limiar durante um sustain não dispara sozinho |
| Alocação | `Process` não aloca nada (medido com `GC.GetAllocatedBytesForCurrentThread`) |
| ChannelMap | USB 3 → índice 2, USB 1 → índice 0, fora da faixa é rejeitado |
| WAV | ida e volta float/PCM24/PCM16, estéreo → mono, chunks extras, reamostragem 44,1k/96k → 48k |

## Modo offline com dados sintéticos

1. Gerar a faixa sintética (mic de bumbo + sample + onsets esperados):

   ```powershell
   dotnet run --project src/SampleDrum.Offline -- --generate-synthetic synthetic-out
   ```

   Gera em `synthetic-out/` (pasta ignorada pelo git):
   - `mic.wav` — 9 s, 48 kHz, mono, PCM24: ruído de fundo a −60 dBFS; golpe forte (−3 dB), suave (−20 dB),
     um com sustain grave longo (300 ms de constante de decaimento), dois golpes a 100 ms um do outro,
     um golpe que começa 3 amostras antes de uma fronteira de bloco comum a 32/64/128/256/480, e uma
     escada de intensidade (−24/−18/−12/−6 dBFS).
   - `kick.wav` — sample sintético de bumbo (~400 ms).
   - `expected.csv` — `sample_index,time_ms,level_db,sustain_ms,label` de cada golpe.

2. Processar e comparar:

   ```powershell
   dotnet run --project src/SampleDrum.Offline -- --mic synthetic-out/mic.wav --sample synthetic-out/kick.wav --out synthetic-out/triggers.wav --expected synthetic-out/expected.csv
   ```

   Saída: número de eventos e, para cada um, tempo (ms), índice de amostra, pico (dBFS), velocity, ganho
   aplicado e a amostra onde o sample começou; depois a comparação esperado × detectado com o erro por
   evento. O WAV `triggers.wav` contém **só** o sample disparado (48 kHz mono; `--format float|pcm24|pcm16`).

   Código de saída: `0` ok, `1` erro de argumento/arquivo, `2` a comparação teve golpes perdidos ou extras.

3. Experimentos úteis:

   ```powershell
   # outro tamanho de bloco (o resultado deve ser idêntico)
   ... --block 480
   # lockout maior que o espaçamento dos golpes próximos: o 2º é suprimido (sai PERDIDO)
   ... --min-interval-ms 150
   # disparo imediato (sem janela de varredura)
   ... --scan-ms 0
   ```

Todas as opções: `dotnet run --project src/SampleDrum.Offline -- --help`.
Números usam ponto decimal (`--threshold-db -27.5`).

Arquivos que não estão em 48 kHz são reamostrados offline (sinc com janela) e convertidos para mono,
com um aviso no console. Os índices de amostra reportados são sempre em 48 kHz.

## Como o detector funciona

Caminho do sinal (mesmo código ao vivo e offline, amostra por amostra, estado guardado entre blocos):

```
entrada → [passa-altas opcional, só na detecção] → |x| → seguidor de pico (ataque instantâneo,
release exponencial EnvelopeReleaseMs) = envelope
```

Máquina de estados:

- **Armado**: quando o envelope atinge `ThresholdDb`, aquela amostra é o *onset*. O detector desarma.
  Se o onset estiver a menos de `MinIntervalMs` do último onset aceito, ele é **suprimido** (sem disparo),
  mas continua desarmado — assim o sustain desse golpe não dispara depois.
- **Varredura** (se `ScanMs > 0`): procura o pico de |x| durante `ScanMs`; o disparo acontece em
  onset + janela, com a velocity desse pico.
- **Desarmado**: rearma quando o envelope fica abaixo de `RearmDb` por `RearmTimeMs` seguidos (histerese +
  tempo de release). Nunca rearma durante a varredura.

Velocity e ganho:

```
velocity = clamp((picoDb − ThresholdDb) / VelocityRangeDb, 0, 1)
ganhoDb  = −(1 − velocity) · Dynamics · VelocityRangeDb + OutputGainDb
```

Com `Dynamics = 1` o sample acompanha o golpe 1:1 em dB dentro da faixa; com `0` todo golpe toca cheio.

### Compromisso da janela de varredura (latência × velocity)

O cruzamento do limiar acontece no início do ataque, quando o sinal ainda está perto do limiar. O pico
real do golpe chega alguns décimos de ms a ~2 ms depois. Por isso:

- `ScanMs = 0`: latência zero adicional (o sample começa na amostra do cruzamento), mas a velocity mede
  quase só o nível do limiar → todos os golpes saem parecidos (use `Dynamics = 0` nesse caso).
- `ScanMs = 1–2 ms` (padrão 2 ms = 96 amostras): velocity confiável; adiciona exatamente esse atraso.
- A latência total ao vivo = latência de entrada ASIO + `ScanMs` + latência de saída ASIO. A janela é
  fixa e determinística, então dá para compensar com delay no canal do microfone na mesa, se preciso.

### Padrões (`TriggerSettings.Default`)

| Ajuste | Padrão | Observação |
|---|---|---|
| `ThresholdDb` | −30 dBFS | |
| `RearmDb` | −40 dBFS | sempre ≤ limiar (normalizado) |
| `RearmTimeMs` | 10 ms | |
| `MinIntervalMs` | 50 ms | |
| `ScanMs` | 2 ms | 0 = imediato |
| `EnvelopeReleaseMs` | 15 ms | |
| `HighPassHz` | 0 (desligado) | ex.: 30–40 Hz para tirar rumble |
| `VelocityRangeDb` | 24 dB | |
| `Dynamics` | 0,75 | |
| `MaxVoices` | 4 | rouba a mais antiga com fade de `StealFadeMs` (2 ms) |

Esses valores são um ponto de partida; serão ajustados com as recomendações do estudo do LSP Trigger e
com gravações reais.

## Contrato com o app (thread de áudio)

- `TriggerEngine.Process(input, output)`: thread de áudio; não aloca, não trava, não faz log/IO. `output`
  vem zerado pelo chamador e o motor **soma** nele. Um disparo dentro do bloco começa no offset exato.
- `UpdateSettings` / `SetSample`: fora da thread de áudio; troca atômica de referência aplicada no início
  do próximo bloco. Coeficientes são calculados em `UpdateSettings`, não no áudio. O array passado em
  `SetSample` passa a pertencer ao motor (não modifique depois). Use `WavFile.ReadMonoAtRate` para carregar.
- `RequestTest(gain)`: qualquer thread; toca no início do próximo bloco, mesmo com `Enabled = false`.
- `TryDequeueEvent`: um único consumidor (UI/log); fila SPSC sem lock de 1024 eventos; se encher, eventos
  são descartados e contados em `DroppedEvents` (o áudio nunca espera).
- `ReadAndResetInputPeak`: pico linear desde a última leitura, para o medidor.
- Desabilitado: o detector continua rodando (estado consistente), mas não dispara; vozes automáticas que
  estavam tocando fazem fade de `StealFadeMs` e depois a saída é zero exato.
- `ChannelMap`: canais da mesa/USB são 1..32; índices ASIO são 0..31 (USB 3 → 2, USB 1 → 0).

## O que é validado onde

- **Compilado e testado offline** (sem hardware): todo o Core, o modo offline e os testes acima.
- **Precisa de hardware** (X32 + X-USB + ASIO): latência real, comportamento com o buffer do driver
  compartilhado com o OBS, níveis reais do microfone de bumbo e ajuste fino dos padrões.
