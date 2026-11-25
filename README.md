# SampleDrum

Reforço de bumbo com sample, gratuito e independente (sem REAPER, Slate ou outra DAW), para o PC Windows da
igreja que também roda o OBS Studio.

O SampleDrum recebe o microfone do bumbo isolado vindo da Behringer X32 pelo X-USB (ASIO, 48 kHz), detecta
cada golpe e devolve **só o sample** para a mesa, num canal de retorno. A equipe de som mistura o sample com o
microfone original na mesa.

## O que faz

- Detecta os golpes do bumbo num canal de entrada ASIO, com limiar, histerese, intervalo mínimo e redisparo
  durante o sustain (ver [docs/research/lsp-trigger.md](docs/research/lsp-trigger.md)).
- Toca um sample WAV (carregado e convertido para 48 kHz mono) num canal de saída ASIO, com volume
  proporcional à força do golpe (ou fixo) e início com precisão de amostra.
- Botão **Testar sample** (toca o sample uma vez, mesmo com o disparo desativado) e
  **Ativar/Desativar disparo** (desativado = retorno em silêncio absoluto).
- Modo **Simulação (sem ASIO)** para testar a interface sem a mesa, `--selftest` sem hardware e um
  **modo offline** que processa arquivos WAV com exatamente o mesmo motor do modo ao vivo.
- Guarda os ajustes em `%AppData%\SampleDrum\settings.json` (canais na numeração da mesa).

## O que não faz

- **Não altera** a taxa de amostragem nem o tamanho de buffer do driver (o OBS compartilha o X-USB):
  se o driver não estiver a 48 kHz, o app recusa iniciar e explica.
- **Não altera** o roteamento da X32 nem do OBS. Toda a configuração da mesa é feita pela equipe.
- Não mistura o microfone original, não grava, não é DAW nem plug-in VST, não usa MIDI.
- Versão 1: só bumbo, uma entrada e uma saída.

## Requisitos

- Windows 11 (x64).
- Driver ASIO do Behringer X-USB instalado, com o X-USB a **48 kHz** (versão do driver a confirmar no ensaio).
- Para rodar: a **build self-contained** (não precisa instalar .NET) **ou** o .NET 10 Desktop Runtime.
- Para compilar: .NET SDK 10 (testado com 10.0.401). `SampleDrum.Core` e o modo offline também compilam e
  rodam em Linux/WSL; o app (WinForms + NAudio.Asio) é só Windows.

## Compilar, testar e rodar

Na raiz do repositório (PowerShell):

```powershell
# tudo (Core, Offline, testes e App)
dotnet build SampleDrum.sln -c Release

# testes do Core (xUnit)
dotnet test SampleDrum.sln -c Release
```

### Modo offline (dados sintéticos ou gravações reais)

```powershell
# gerar mic sintético + sample + onsets esperados em synthetic-out/
dotnet run -c Release --project src/SampleDrum.Offline -- --generate-synthetic synthetic-out

# processar, gravar o WAV só com o trigger e comparar com o esperado
dotnet run -c Release --project src/SampleDrum.Offline -- --mic synthetic-out/mic.wav --sample synthetic-out/kick.wav --out synthetic-out/triggers.wav --expected synthetic-out/expected.csv

# todas as opções
dotnet run -c Release --project src/SampleDrum.Offline -- --help
```

Código de saída: `0` ok, `1` erro de argumento/arquivo, `2` a comparação teve golpes perdidos ou extras.
Detalhes do detector, dos padrões e dos testes: [docs/README-dev.md](docs/README-dev.md).

### App

```powershell
# rodar a interface
dotnet run -c Release --project src/SampleDrum.App

# teste sem hardware (simulação + motor + conversores ASIO); grava a saída num arquivo
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --selftest > selftest.txt; Get-Content selftest.txt

# listar os drivers ASIO instalados
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --list-drivers > drivers.txt; Get-Content drivers.txt

# build self-contained para levar ao PC da igreja (pasta única, ~120 MB, sem instalar .NET)
dotnet publish src/SampleDrum.App -c Release -r win-x64 --self-contained -o publish\SampleDrum-win-x64
```

O app é um programa de janela (WinExe): para `--selftest` e `--list-drivers`, redirecione a saída para um
arquivo como acima (no console ela pode aparecer misturada ao prompt). `--selftest` termina com
`RESULTADO: OK` e código de saída 0. A pasta `publish\` não deve ser versionada; copie-a inteira para o PC
e rode `SampleDrum.App.exe`.

Detalhes do ASIO/NAudio: [docs/asio-naudio.md](docs/asio-naudio.md).

## Perfil de roteamento padrão (cena de teste)

A mesa e o X-USB numeram canais a partir de **1**; a API ASIO usa índices a partir de **0**:
`índice ASIO = canal USB − 1`. O app mostra os dois, por exemplo "Entrada USB 3 (índice ASIO 2)".

| Função | Caminho na mesa | Canal USB | Índice ASIO |
|---|---|---|---|
| Microfone do bumbo | AES50 B21 → **CH 21** | — | — |
| Envio para o app (entrada do PC) | **User Out 3** (fonte: CH 21) → entrada USB 3 do PC | 3 | **entrada 2** |
| Retorno do app (saída do PC) | saída USB 1 do PC → **Card In 1** → **Aux 1** | 1 | **saída 0** |

- O bloco **Aux In** foi remapeado para **Card 1-2**, de modo que Card In 1 chega no **Aux 1**.
  O **Aux 4 não é alterado**.
- O **OBS** recebe os **MixBus 13/14** pelas **USB 1/2 no sentido contrário** (mesa → PC, entradas USB 1/2
  do PC). Isso não conflita com o retorno do app, que usa a **saída** USB 1 (PC → mesa).
- Na mesa, o Aux 1 (sample) é misturado com o CH 21 (microfone original) pela equipe.

## Primeiro ensaio na igreja (checklist)

Use a **cena de teste** da X32. O OBS fica aberto e capturando normalmente, com a **transmissão pública
DESLIGADA**. Leve a build self-contained e anote tudo que aparecer na linha "Buffer/latência" do app.

1. **Sample chega só no Aux 1**
   - Abra o app, escolha o driver ASIO do X-USB, confira "Entrada USB 3 (índice ASIO 2)" e
     "Saída USB 1 (índice ASIO 0)".
   - Carregue o WAV do sample (**Escolher WAV...**), clique **Iniciar áudio** e depois **Testar sample**.
   - Confirme que o sample aparece **só no Aux 1** (medidores da mesa) e em nenhum outro canal ou bus.
2. **OBS continua igual**
   - Com o app rodando, confirme que o áudio dos buses 13/14 continua chegando ao OBS **sem mudança**
     (nível, sem cortes, sem atraso novo, sem o sample no OBS a menos que a equipe o mande para lá).
3. **Disparo automático**
   - Clique **Ativar disparo** e peça ao baterista para tocar suave e forte, golpes isolados e duplas rápidas.
   - Verifique: nenhum golpe duplo, nenhum golpe perdido, nenhum estalo/dropout, nada mudou no OBS.
     Ajuste o limiar (e, se preciso, o grupo **Avançado**) até ficar estável.
   - **Meça o atraso** entre o CH 21 e o Aux 1: grave os dois em duas faixas de uma gravação local
     (por exemplo duas saídas USB/card da X32 gravadas no PC ou no cartão da mesa, **sem mexer no roteamento
     do OBS**) e exporte um WAV estéreo. Depois:

     ```powershell
     # onsets do microfone (faixa 1)
     dotnet run -c Release --project src/SampleDrum.Offline -- --mic gravacao.wav --channel 1 --events-out ch21.csv
     # onsets do retorno (faixa 2) comparados com os do microfone: o "erro" por golpe é o atraso
     dotnet run -c Release --project src/SampleDrum.Offline -- --mic gravacao.wav --channel 2 --threshold-db -40 --expected ch21.csv --tolerance-ms 50
     ```

     Anote o atraso medido junto com o buffer ASIO e a latência do driver mostrados na linha de status.
4. **Gravar o bumbo cru para calibração**
   - Grave alguns minutos da entrada USB 3 (bumbo cru, suave e forte, com a banda tocando para ter o
     vazamento real). Esse arquivo calibra o limiar, a faixa de velocity e o intervalo mínimo no modo offline
     (`--mic bumbo.wav`), sem precisar da mesa.

## Depende do ensaio presencial

Ainda não confirmado (sem hardware até agora):

- Versão do driver ASIO do X-USB e o formato nativo das amostras (esperado `Int32LSB`; o app aceita também
  `Float32LSB`, `Int16LSB` e `Int24LSB`).
- Tipo de fonte de áudio usada no OBS (ASIO, WASAPI/WDM) e se o driver X-USB aceita o app e o OBS ao mesmo
  tempo (driver compartilhado).
- Latência real ida e volta (buffer configurado no painel do driver + conversões + `DetectTimeMs`).
- Níveis reais do bumbo e do vazamento, e portanto os padrões finais de limiar, velocity, intervalo mínimo e
  redisparo.

## Documentação

- [docs/README-dev.md](docs/README-dev.md) — detector, padrões, modo offline e testes.
- [docs/asio-naudio.md](docs/asio-naudio.md) — ASIO com NAudio, threads, formatos e índices.
- [docs/research/lsp-trigger.md](docs/research/lsp-trigger.md) — pesquisa sobre o LSP Trigger (referência
  de comportamento; nenhum código do LSP, que é LGPL-3, foi copiado).
