# Pesquisa: detector do LSP Trigger (referência para o SampleDrum)

Data: 2026-11-25. Etiquetas: **[fonte]** = lido no código ou manual oficial; **[observação]** = constatado ao ler/medir algo concreto; **[inferência]** = conclusão nossa (matemática ou de projeto), ainda não validada com áudio real.

## Ferramentas e fontes

- **REA** (morluto/rea, pacote npm `rea-agents` **6.7.0**, licença MIT): instalado no Windows (`%APPDATA%\npm`), e o servidor MCP responde nesta sessão (`mcp__rea__*`). No WSL (Ubuntu) **não há instalação nativa**: `rea` resolve para o shim do Windows via PATH (`/mnt/c/.../npm/rea`) e roda com o node do Linux (v22.22.1; o REA exige `^22.19.0`). `rea --version` → 6.7.0 [observação]. O WSL tem `ps` (procps-ng 4.0.4), `objdump`, `gdb` e `java`, mas nenhum provedor nativo (Ghidra/Rizin/Hopper).
  - Para uma instalação nativa no WSL (não executada, depende de aprovação): `npm config set prefix ~/.npm-global && export PATH=~/.npm-global/bin:$PATH && npm install --global rea-agents && rea --help` (sem sudo, pois `/usr/local` é do root). Para análise nativa, ainda é preciso um provedor (Ghidra ou Rizin) já instalado; veja `docs/installation.md` do REA. `npx rea-agents setup` só registra o MCP no agente.
  - **A análise de binário com o REA não foi necessária**: o código-fonte oficial (LGPL) responde a todas as perguntas, e o binário oficial é compilado desse mesmo código. Nenhum binário foi baixado.
- Código oficial, clonado em diretório temporário (só leitura, nada compilado nem executado):
  - `lsp-plugins/lsp-plugins-trigger` tag **1.0.38** (`ac836dc81d10dbb8d1e1f83c70e05190e8be3da8`)
  - `lsp-plugins/lsp-dsp-units` tag **1.0.39** (`b7a2475851bdf86cafcd5fb82115245696be5257`): `Sidechain`
  - `lsp-plugin-fw` 1.0.41 e `lsp-dsp-lib` 1.1.2 (só para contexto)
- Manual: `src/doc/manuals/plugins/trigger.php` (no repositório) = https://lsp-plug.in/?page=manuals&section=trigger_mono (conferi que o texto é o mesmo) [observação].

## Licença

[fonte] `lsp-plugins-trigger` e `lsp-dsp-units`: **GNU LGPL v3 ou posterior** (cabeçalhos dos `.cpp` e `COPYING.LESSER`). Copiar ou traduzir código para C# faria do trecho uma obra derivada LGPL, o que obriga a distribuir sob LGPL e a permitir que o usuário substitua a parte LGPL. **Decisão: não copiamos nem traduzimos código.** Reimplementamos a partir do comportamento descrito aqui (ideias e algoritmos gerais como limiar, histerese e RMS não são protegidos) e citamos o LSP como referência. Nenhum trecho do LSP neste repositório.

## Como o LSP Trigger funciona

**Cadeia do sidechain** [fonte] (`lsp-dsp-units` `src/main/util/Sidechain.cpp`, `Sidechain::process` l.439-556; `trigger::update_settings` l.478-566):
1. EQ de pré-processamento: HPF e LPF Butterworth (desligado, 12, 24 ou 36 dB/oct); depois |x|; depois `preamp` (ganho).
2. Modo (padrão **RMS**, `MODE_DFL=1`): *Peak* = |x| amostra a amostra, sem suavização; *RMS* = janela deslizante de `reactivity` ms (soma dos quadrados que entra e sai, com recálculo exato a cada `REFRESH_RATE`=8192 amostras para evitar deriva numérica); *LPF* = filtro de um polo que chega a ~70,7% em `reactivity` ms; *SMA* = média móvel de |x|. Reatividade padrão de **20 ms** (0 a 250).

**Máquina de estados** [fonte] (`trigger.cpp` `trigger::process_samples` l.362-441), avaliada por amostra:
- `OFF` → `DETECT` quando `nível ≥ detect`.
- `DETECT`: se o nível cai abaixo de `detect`, volta a `OFF` (*debounce*: rejeita picos curtos). Se fica acima por `detect time`, dispara o **note-on** nessa amostra e passa a `ON`.
- `ON` → `RELEASE` quando `nível ≤ release`, onde `release = detect × release relativo` (o release é **relativo ao limiar**, não ao pico do golpe).
- `RELEASE`: se o nível volta a subir acima de `release`, retorna a `ON` **sem disparar**. Se fica abaixo por `release time`, vai a `OFF`, e só então o detector está **rearmado**.
- Padrões (`include/private/meta/trigger.h` l.54-118 e `src/main/meta/trigger.cpp` l.141-157): detect **−12 dB**, detect time **5 ms** (0 a 20), release relativo **−3 dB**, release time **10 ms** (0 a 100), dynamics **10%**, dynamics range 1 **+6 dB**, range 2 **−36 dB**.

**Separar um novo ataque do sustain e evitar disparo duplo**:
- [fonte] Não existe "intervalo mínimo" explícito. A proteção vem da histerese (release < detect), do tempo contínuo abaixo de release e da suavização do envelope (RMS ou LPF).
- [inferência] O menor intervalo entre golpes é aproximadamente detect time + tempo até o envelope cair abaixo de release + release time + detect time. Um segundo golpe que chega enquanto o sustain do primeiro ainda está acima de `release` **é perdido**. Como o release é absoluto (relativo ao limiar), um bumbo ressonante tocado forte pode passar centenas de ms acima de release. É o ponto fraco para golpes rápidos.
- [inferência] No modo *Peak*, |x| de um bumbo de ~50-60 Hz passa por zero a cada ~8-10 ms. Com release time menor que meio período, o detector rearma no meio do sustain e **dispara em duplicidade**. Com detect time longo, o `DETECT` aborta nos cruzamentos por zero. Por isso o padrão é RMS 20 ms. Precisamos de um envelope com retenção (*peak-hold* com decaimento) e não de |x| cru.

**Velocidade e dinâmica** [fonte] (`process_samples` l.390-400; `trigger_kernel::trigger_on` l.800-842):
- A velocidade usa o nível do sidechain **na amostra em que o detect time termina** (não o pico do golpe): `v = 0,5·(nível/detect)^dynamics`, que é mapeado em escala log entre range 2 (piso) e range 1 (teto) para 0..1, com saturação.
- [inferência] Em dB isso vira `vel = (dynamics·ΔdB − 6,02 − piso_dB)/(teto_dB − piso_dB)`, onde ΔdB é o quanto o nível passou do limiar. Com os padrões: 0,714 no limiar e só 0,762 a +20 dB, ou seja, quase sem dinâmica. Com dynamics de 100%, satura a +12 dB. A velocidade é **linear em dB** acima do limiar.
- Ganho da amostra = `vel / velocidade máxima da camada` (linear), × makeup × ganho do arquivo. Há até 8 camadas escolhidas por busca binária. "Dynamics" e "Drift" do kernel são humanização aleatória (padrão 0), que não usaremos.
- [inferência] Como a medida vem do fim do detect time, um ataque com subida lenta ou um detect time curto subestima a força do golpe.

**Vozes e golpes próximos** [fonte]: cada note-on cria uma nova reprodução que se **sobrepõe** às anteriores (`PLAYBACKS_MAX = 8192`). O note-off **não** corta a amostra (a chamada a `sKernel.trigger_off` está comentada em `trigger::trigger_off` l.629-652). O `pre-delay` (0 a 100 ms) só atrasa.

**Fronteira de bloco** [fonte]: estado, contador e buffer do sidechain persistem entre blocos. O processamento é dividido em pedaços de ≤ `BUFFER_SIZE` = 4096 amostras e a amostra começa com `delay = timestamp` dentro do pedaço, ou seja, com precisão de amostra. [observação] O timestamp do MIDI usa o índice relativo ao pedaço (`trigger_on(i, …)`, sem somar `offset` em `trigger::process`), o que só erra com blocos > 4096. Lição para nós: usar sempre o índice absoluto de amostra no evento.

## Recomendações para o nosso detector (reimplementação própria)

Pipeline sugerido: HPF → |x| → envelope *peak-hold* (ataque instantâneo, decaimento exponencial) → máquina de estados no estilo do LSP + **intervalo mínimo explícito** + **redisparo por subida** durante o sustain → velocidade pelo pico em dB. Tudo [inferência], a validar com a gravação real.

| Parâmetro (sugestão de nome) | Padrão bumbo @ 48 kHz | Faixa | Função |
|---|---|---|---|
| `HighPassHz` | 40 Hz, 12 dB/oct | off, 20-200 | tira ronco e vibração de palco |
| `LowPassHz` | off (150-250 Hz se houver vazamento de caixa/tons) | off, 100-8000 | rejeita vazamento; atenção ao atraso de grupo |
| `EnvelopeReleaseMs` | 20 ms (960 amostras) | 5-100 | maior que 1 período da fundamental do bumbo |
| `DetectLevelDb` | −24 dBFS (calibrar) | −60…0 | ~12-18 dB abaixo do golpe mais fraco e ≥ 10 dB acima do vazamento |
| `DetectTimeMs` | 0,5 ms (24 amostras) | 0-5 | *debounce*; é latência adicionada |
| `ReleaseLevelDb` (relativo ao detect) | −6 dB | −24…0 | histerese |
| `ReleaseTimeMs` | 15 ms (720 amostras) | 0-100 | tempo abaixo de release para rearmar |
| `MinIntervalMs` | 35 ms (1680 amostras) | 10-150 | trava absoluta contra disparo duplo |
| `RetriggerRiseDb` | 9 dB (0 = desligado) | 0-24 | permite novo golpe no sustain se o envelope subir X dB acima do mínimo desde o último pico (após `MinIntervalMs`) |
| `VelocityRangeDb` | 24 dB | 6-48 | dB acima do limiar mapeados linearmente para velocidade 0..1 |
| `MinGainDb` | −20 dB | −40…0 | ganho da amostra com velocidade 0 (velocidade 1 = 0 dB); `FixedVelocity` para desligar a dinâmica |
| `MaxVoices` | 4, roubo da mais antiga com fade de 3 ms | 1-16 | sobreposição pré-alocada, sem alocação no thread de áudio |

Calibração com a gravação real: medir o pico do vazamento e do ruído com o bumbo parado, e o pico do golpe mais fraco e do mais forte. Pôr `DetectLevelDb` entre os dois primeiros. Ajustar `VelocityRangeDb` ≈ (forte − fraco) em dB. Reduzir `MinIntervalMs` até o menor intervalo real de duplas do baterista, com folga de ~10 ms.

## O que vamos testar no nosso detector

1. **Silêncio** (e ruído a −70 dBFS): zero eventos; o envelope não deriva.
2. **Golpe único com sustain longo** (seno 55 Hz decaindo em ~400 ms, ataque com clique): exatamente 1 evento, inclusive com `ReleaseTimeMs` mínimo (verifica que os cruzamentos por zero não rearmam).
3. **Dois golpes próximos**: intervalo < `MinIntervalMs` (ex.: 20 ms) dá 1 evento; intervalo de 40, 60 e 100 ms dá 2 eventos, mesmo com o sustain do primeiro acima de release (exercita `RetriggerRiseDb`).
4. **Golpe na fronteira de bloco**: o mesmo sinal processado em blocos de 32, 64, 128, 256, 480, 512 e 1024 e sinal inteiro gera eventos idênticos (mesmo `SampleIndex`, mesma velocidade) e saída idêntica amostra a amostra.
5. **Velocidade**: golpes a −30, −18 e −6 dBFS dão velocidades crescentes e monotônicas; ganho dentro de `[MinGainDb, 0 dB]`; acima de limiar + `VelocityRangeDb` satura em 1.
6. **Ressonância grave e vazamento**: ringing de 50 Hz contínuo abaixo do limiar, caixa ou ton (200 Hz a 1 kHz) vazando a −20 dB do bumbo: nenhum disparo falso; com `LowPassHz` ligado o vazamento não dispara.
7. **Pico isolado** (clique de 1-2 amostras acima do limiar): rejeitado quando `DetectTimeMs` > duração do clique.
8. **Latência**: tempo entre o onset sintético e o início da amostra na saída ≤ detect time + 1 amostra (sem contar o buffer ASIO).
9. **Golpes em sequência rápida** além de `MaxVoices`: sem clique (fade no roubo) e sem alocação.

(Testes 1-9: offline/xUnit com sinais sintéticos. A calibração de valores depende da **gravação real** do bumbo, que precisa do hardware.)
