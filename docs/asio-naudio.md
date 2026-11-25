# ASIO com NAudio no SampleDrum

Este documento registra a versão do NAudio usada pelo `SampleDrum.App`, a API ASIO
**confirmada contra o código-fonte da versão instalada** e como a numeração da mesa
(X32 / X-USB) se traduz em índices da API.

## Versão

| Pacote        | Versão | Licença | Alvo do pacote |
|---------------|--------|---------|----------------|
| `NAudio.Asio` | 3.3.0  | MIT     | `net9.0` (Windows-only via `[SupportedOSPlatform("windows")]`) |

- 3.3.0 é a última versão estável no NuGet em 25/11/2026 (existe `3.4.0-preview.1`, não usada).
- A partir do NAudio 3.x o meta-pacote `NAudio` **não inclui mais** o `NAudio.Asio`;
  por isso o App referencia `NAudio.Asio` diretamente (ele traz `NAudio.Core` como dependência).
- Como foi conferido: XML de documentação do pacote
  (`~/.nuget/packages/naudio.asio/3.3.0/lib/net9.0/NAudio.Asio.xml`) e o código-fonte na tag
  `v3.3.0` do repositório `naudio/NAudio` (`src/NAudio.Asio/AsioDevice.cs`, `AsioDriverExt.cs`,
  `AsioDriver.cs`, `AsioOut.cs`, `AsioProcessBuffers.cs`). Nenhum código do NAudio foi copiado.

## Duas APIs na 3.3.0

### 1. `AsioOut` (legada, mantida como "fachada")

- `AsioOut.GetDriverNames()` – nomes dos drivers.
- `new AsioOut(string driverName)`.
- `InitRecordAndPlayback(IWaveProvider? waveProvider, int recordChannels, int recordOnlySampleRate)`.
- `InputChannelOffset` / `ChannelOffset` – deslocamento do primeiro canal de entrada / saída
  (aplicado com `SetChannelOffset` depois de `CreateBuffers`).
- Evento `AudioAvailable` com `AsioAudioAvailableEventArgs`:
  `InputBuffers` (`IntPtr[]`), `OutputBuffers` (`IntPtr[]`), `WrittenToOutputBuffers` (`bool`),
  `SamplesPerBuffer`, `AsioSampleType`, `GetAsInterleavedSamples(float[])`.
- `ShowControlPanel()`, `IsSampleRateSupported(int)`, `DriverInputChannelCount`,
  `DriverOutputChannelCount`, `FramesPerBuffer`.
- O caminho de gravação legado está marcado como obsoleto internamente (`CS0618`).
- **Taxa de amostragem**: `InitCore` chama `SetSampleRate(desejada)` **se** a taxa atual do driver
  for diferente da do `IWaveProvider`. Ou seja: com AsioOut, um provider a 48 kHz num driver a
  44,1 kHz **mudaria a taxa do driver** – o que não podemos fazer (o OBS compartilha o X-USB).

### 2. `AsioDevice` (nova, usada pelo SampleDrum)

- `AsioDevice.GetDriverNames()` – lê as subchaves de `HKLM\SOFTWARE\ASIO`.
- `AsioDevice.Open(string driverName)` – instancia o driver COM (`CoCreateInstance`, **exige
  thread STA**, por isso tudo é aberto na thread da UI WinForms) e chama `ASIOInit`.
  - Nome inexistente: `ArgumentException("Driver Name … doesn't exist")`.
  - `ASIOInit` falhou (driver em uso por outro programa sem multi-cliente, interface
    desconectada, driver só x86): `InvalidOperationException` com a mensagem do driver ou uma
    dica genérica.
  - Falha de COM: `InvalidOperationException("Unable to instantiate ASIO…")`.
- `Capabilities` (`AsioDriverCapability`): `NbInputChannels`, `NbOutputChannels`,
  `SampleRate` (double), `BufferPreferredSize`, `InputChannelInfos[]`/`OutputChannelInfos[]`
  (cada `AsioChannelInfo` tem `name` e `type` = `AsioSampleType`).
- `CurrentSampleRate` (int) – taxa atual informada pelo driver.
- `ShowControlPanel()` – abre o painel do driver.
- `InitDuplex(AsioDuplexOptions)`:
  - `InputChannels` / `OutputChannels`: `int[]` com **índices físicos 0-based**; podem ser
    não contíguos. Validados contra `NbInputChannels`/`NbOutputChannels`
    (`ArgumentOutOfRangeException`) e o formato nativo precisa ser único entre os canais
    escolhidos de cada lado (`NotSupportedException`).
  - `SampleRate` (`int?`): **se `null`, usa a taxa atual do driver e não chama `SetSampleRate`**
    (o código só chama `SetSampleRate` quando a taxa pedida difere da atual).
  - `BufferSize` (`int?`): se `null`, usa `BufferPreferredSize` do driver em `CreateBuffers` –
    não altera a configuração de buffer do driver.
  - `Processor`: `AsioProcessCallback` = `void (in AsioProcessBuffers buffers)`; roda na thread
    de tempo real do driver.
- `AsioProcessBuffers` (`ref struct`, só vale durante o callback):
  `Frames`, `SampleRate`, `GetInput(i)` (float, convertido sob demanda),
  `GetOutput(i)` (float, zerado pela biblioteca antes do callback),
  `RawInput(i)` / `RawOutput(i)` (bytes nativos, `Format` = `AsioSampleType`, sem cópia).
  Usar `RawOutput` faz a biblioteca **pular** a conversão float→nativo daquele canal.
  O índice `i` é a posição no array `InputChannels`/`OutputChannels`, não o canal físico.
- `Start()`, `Stop()` (não pode ser chamado dentro do callback), `Dispose()` (espera o
  callback em andamento terminar).
- Eventos (despachados no `SynchronizationContext` capturado em `Open`, ou seja, na UI):
  `Stopped` (inclui exceção quando o callback lançou e a biblioteca parou o driver),
  `DriverResetRequest` (mudança de taxa/buffer pelo painel), `LatenciesChanged`,
  `ResyncOccurred`.
- Conversores públicos: `AsioNativeToFloatConverter` / `AsioFloatToNativeConverter`
  (Int16LSB, Int24LSB, Int32LSB, Float32LSB).
- Caminho por buffer (`AsioDriverExt.BufferSwitchCallBack` → `OnBufferUpdateDuplex`): só
  estruturas de pilha e arrays pré-alocados em `InitDuplex`; sem alocação no heap.

## Como o SampleDrum usa (AsioDuplexHost)

1. `AsioDevice.Open(nome)` na thread da UI.
2. Verifica `CurrentSampleRate == 48000`; se não, **recusa** com mensagem clara (não muda a taxa).
3. Verifica os índices contra `NbInputChannels`/`NbOutputChannels`.
4. Verifica o `AsioSampleType` dos dois canais: aceita `Int32LSB` e `Float32LSB`
   (também `Int16LSB` e `Int24LSB`); qualquer outro formato gera erro claro antes de iniciar.
5. `InitDuplex` com `InputChannels = [entrada]`, `OutputChannels = [saída]`,
   `SampleRate = null`, `BufferSize = null`.
6. Aloca os buffers float com `FramesPerBuffer` amostras.
7. No callback: `RawInput(0)` → conversão própria para o buffer float pré-alocado → limpa o
   buffer de saída → `IBlockProcessor.Process(entrada, saída)` → conversão própria para o
   formato nativo em `RawOutput(0)`. Sem alocação, lock, log, E/S ou UI.
8. Ao receber `DriverResetRequest` (alguém mudou taxa/buffer no painel), o host **para** e
   avisa; ao iniciar de novo, a taxa é verificada outra vez.

### Threads dos eventos (conferido no código da 3.3.0)

- `asioMessage(kAsioResetRequest)` chega numa thread do driver, mas o NAudio **não** chama
  `DriverResetRequest` nela: `AsioDevice.DispatchEvent` faz `SynchronizationContext.Post` para o
  contexto capturado em `Open` (ou `ThreadPool.QueueUserWorkItem` se não houver contexto).
- `Stopped` idem (`RaiseStopped`). Quando o callback lança exceção, o NAudio para o driver via
  `ThreadPool` (nunca dentro do callback; `Stop()` chamado do callback lança
  `InvalidOperationException` para evitar deadlock).
- Mesmo assim o `AsioDuplexHost` captura o `SynchronizationContext` em `Start()` (thread da UI)
  e repassa os dois eventos para ele antes de parar/descartar o driver e disparar `Faulted`,
  ignorando eventos de um dispositivo antigo. O `MainForm` sempre trata `Faulted` via
  `BeginInvoke` (protegido contra janela já fechada), pois a simulação o dispara na thread dela.
- Caminho do callback sem alocação: `BufferSwitchTimeInfoCallBack` lê o `AsioTime` sem
  marshalling de strings e usa só structs (`AsioBufferTimingInfo`, `AsioTimeCodeInfo`);
  `AsioProcessBuffers`, `AsioRawInputBuffer`/`AsioRawOutputBuffer` são `ref struct`.

## Numeração da mesa × índices da API

A mesa e o X-USB numeram canais a partir de **1**; a API ASIO usa índices a partir de **0**:

```
índice ASIO = canal USB da mesa − 1        (canal USB 1..32  ↔  índice 0..31)
```

Perfil padrão (cena de teste):

| Função  | Na mesa                                   | Canal USB | Índice ASIO |
|---------|-------------------------------------------|-----------|-------------|
| Entrada | User Out 3 → entrada USB 3 do PC          | 3         | 2           |
| Retorno | saída USB 1 do PC → Card In 1 → Aux 1     | 1         | 0           |

Na interface os campos mostram a numeração da mesa, por exemplo
"Entrada USB 3 (índice ASIO 2)". A conversão usa `ChannelMap.DeskToApiIndex` do Core e o
índice é conferido contra o número de canais que o driver informa.

## O que só pode ser confirmado com o hardware

- Formato nativo real do X-USB (`Int32LSB` é o esperado; o código também aceita `Float32LSB`,
  `Int16LSB` e `Int24LSB`).
- Se o driver X-USB permite abrir o ASIO enquanto o OBS está usando o mesmo dispositivo.
- Latência total ida-e-volta e o tamanho de buffer preferido configurado no painel.
