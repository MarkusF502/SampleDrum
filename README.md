# SampleDrum

Reforço de bateria com samples, gratuito e independente (sem REAPER, Slate ou outra DAW), para o PC Windows da
igreja que também roda o OBS Studio. Versão 2: **bumbo, caixa, tom e surdo**.

O SampleDrum recebe o microfone isolado de cada peça vindo da Behringer X32 pelo X-USB (ASIO, 48 kHz), detecta
cada golpe e devolve **só o sample** daquela peça para a mesa, num retorno próprio. A equipe de som mistura os
samples com os microfones originais na mesa.

## O que faz

- Quatro peças **independentes** (Bumbo, Caixa, Tom, Surdo): cada uma tem entrada, retorno, detector, vozes,
  sample, ganho e ajustes próprios. Golpes de peças diferentes no mesmo bloco de áudio são tratados com precisão
  de amostra, sem interferência entre elas.
- Detecção com limiar, histerese, intervalo mínimo e redisparo durante o sustain (ver
  [docs/research/lsp-trigger.md](docs/research/lsp-trigger.md)); volume do sample proporcional à força do golpe
  (ou fixo).
- Um botão **Ativar kit / Desativar kit** (o kit sempre começa **desativado**; desativado = retornos em silêncio
  absoluto) e, por peça, a caixa **Ativa**, o botão **Testar** (toca o sample uma vez, mesmo com o kit
  desativado), medidor de entrada e LED de disparo.
- **Samples por peça** (WAV de qualquer taxa/canais, convertido para 48 kHz mono fora da thread de áudio) e
  **presets de kit** (arquivos JSON). Trocar um preset troca as quatro peças **no mesmo bloco**: os golpes já
  disparados terminam com o sample antigo, os próximos usam o novo. Se algum arquivo do preset falhar, nada é
  trocado e o kit atual continua.
- **Perfis de roteamento** (Igreja / Simulação) editáveis, com **validação visível** antes de iniciar.
- Modo **Simulação (sem ASIO)**, `--selftest` sem hardware e **modo offline** que processa arquivos WAV com o
  mesmo motor do modo ao vivo. A simulação da janela alimenta as **quatro peças** (kit sintético, quatro WAVs de
  microfone ou um WAV multicanal) e pode tocar uma **prévia** nos fones/alto-falantes do PC. Ver também
  [docs/harness.md](docs/harness.md) (`--harness`, `--simulation`).
- **Painel do celular** (opcional, desligado por padrão): pelo navegador do celular na rede da igreja, trocar o
  sample de uma peça ou aplicar um preset com o áudio rodando, depois de parear com um código mostrado no PC. Ver
  [Painel do celular](#painel-do-celular).
- Guarda tudo em `%AppData%\SampleDrum\settings.json` (versão 2; canais na numeração da mesa).

## O que não faz

- **Não altera** a taxa de amostragem nem o tamanho de buffer do driver (o OBS compartilha o X-USB):
  se o driver não estiver a 48 kHz, o app recusa iniciar e explica.
- **Não altera** o roteamento da X32 nem do OBS. Toda a configuração da mesa é feita pela equipe.
- **Nunca usa** as entradas USB 1/2 do PC (OBS, MixBus 13/14) nem o Aux 4 "Celular".
- Não mistura os microfones originais, não grava, não é DAW nem plug-in VST, não usa MIDI.

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
# acentos certos na saída: o Windows PowerShell 5.1 lê a saída com a página de código OEM (850)
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# rodar a interface
dotnet run -c Release --project src/SampleDrum.App

# interface já em simulação: perfil "Simulação", host sem ASIO e kit sintético carregado
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --simulation

# cenário de teste completo sem a mesa (relatório e retornos gravados na pasta; código 0 = tudo certo)
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --harness C:\temp\sd-teste | Out-Host; $LASTEXITCODE

# trocas de kit durante o processamento (preset/peça/falha) verificadas sem a mesa; código 0 = tudo certo
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --harness-swap C:\temp\sd-troca | Out-Host; $LASTEXITCODE

# teste sem hardware; grava a saída num arquivo e mostra o código de saída
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --selftest | Out-File selftest.txt; Get-Content selftest.txt; $LASTEXITCODE

# listar os drivers ASIO instalados
.\src\SampleDrum.App\bin\Release\net10.0-windows\SampleDrum.App.exe --list-drivers | Out-Host

# build self-contained para levar ao PC da igreja (pasta única, ~120 MB, sem instalar .NET)
dotnet publish src/SampleDrum.App -c Release -r win-x64 --self-contained -o publish\SampleDrum-win-x64
```

O app é um programa de janela (WinExe) e o **PowerShell não espera** por um programa de janela quando a saída é
redirecionada com `>` (nem com `$x = ...exe`): o comando volta na hora, o arquivo fica vazio e `$LASTEXITCODE` vem
vazio. Use sempre `| Out-File arquivo.txt` ou `| Out-Host` como acima (no CMD: `start /wait ... & echo %ERRORLEVEL%`).
`--selftest` termina com `RESULTADO: OK` e código de saída 0. Mais detalhes em [docs/harness.md](docs/harness.md). A pasta `publish\` não deve
ser versionada; copie-a inteira para o PC e rode `SampleDrum.App.exe`.

Detalhes do ASIO/NAudio: [docs/asio-naudio.md](docs/asio-naudio.md).

## A janela

- **Áudio**: driver (ou "Simulação (sem ASIO)"), **Perfil de rotas** (Igreja / Simulação) com **Editar
  rotas...**, a **lista de validação** do roteamento, Iniciar/Parar áudio e a linha de buffer/latência.
- **Simulação (sem ASIO)** (só aparece com esse driver escolhido): fonte dos microfones (kit sintético com
  semente, 4 WAVs de microfone ou WAV multicanal com o canal de cada peça), tamanho do bloco, repetir, **Carregar kit
  sintético**, e a **prévia nos fones** (volume, quais peças entram, microfones por baixo). A fonte e o bloco valem
  no próximo "Iniciar áudio"; a prévia muda na hora. Na simulação só as peças **roteadas no perfil** recebem sinal
  (perfil Simulação = as quatro; perfil Igreja = só o bumbo enquanto os outros estão pendentes).
  **Carregar kit sintético** troca samples e ajustes por os da simulação **sem salvar** nada na configuração; ao
  escolher um driver ASIO, iniciar o ASIO, escolher um sample ou aplicar um preset, o kit e a calibração do operador
  voltam automaticamente. Uma execução com `--simulation` **não grava nada** no `settings.json` (não cria nem altera
  o arquivo); nela, samples e presets vêm de uma biblioteca de demonstração temporária.
- **Kit**: **Ativar kit / Desativar kit**; preset (**Aplicar preset**, **Abrir arquivo...**,
  **Salvar preset...**); pasta de samples.
- **Painel do celular**: ligar/parar, rede e porta, endereço + QR code, código de pareamento, celulares
  autorizados e "Permitir teste pelo celular" (ver [Painel do celular](#painel-do-celular)).
- Uma faixa por peça (**Bumbo, Caixa, Tom, Surdo**): **Ativa**, rota na numeração da mesa ("Entrada USB 3
  (índice ASIO 2)" ou "pendente"), medidor + LED (verde = disparo, azul = teste), sample (**Escolher WAV...**,
  **Sem sample**), **Ganho (dB)**, **Testar**, limiar/rearme/intervalo mínimo e o grupo **Avançado ▸**
  (passa-altas, passa-baixas, tempo de detecção, janela de velocidade, tempo de rearme, redisparo, faixa de
  velocidade, ganho mínimo, velocidade fixa, restaurar padrões da peça).

Presets ficam em `%AppData%\SampleDrum\Presets` (um `.json` por preset) e a pasta de samples padrão é
`%AppData%\SampleDrum\Samples` (pode ser trocada). Um preset guarda, por peça, o sample, o ganho e só os
ajustes de detecção que diferem do padrão da peça.

## Painel do celular

Pelo navegador do celular, **na mesma rede do PC**, o operador troca o sample de uma peça ou aplica um preset de
kit inteiro com o áudio rodando. Não precisa instalar app, criar conta nem ter internet: a página vem do próprio
SampleDrum. O áudio continua sendo processado só no PC e **não depende da rede**: se o Wi-Fi cair ou o painel for
desligado, o áudio e o kit ativo seguem como estão.

### Ligar e parear

1. No grupo **Painel do celular**, escolha a **Rede** (só aparecem adaptadores ativos com IPv4 de rede local;
   adaptadores virtuais como `vEthernet (WSL)`, VPN e 169.254.x ficam de fora; se houver mais de uma, escolha a da
   igreja) e a **Porta** (padrão **8750**). Clique em **Ativar painel**. O painel começa **desligado**; a escolha
   fica salva para a próxima vez (nunca numa execução com `--simulation`).
   - Se a porta estiver ocupada, aparece uma mensagem clara: escolha outra porta e ative de novo. O áudio não é
     afetado.
   - "Só este PC (localhost)" serve para testar no próprio PC. "Todas as redes (0.0.0.0)" existe, mas **não** é
     recomendado.
2. O PC mostra o **endereço** (`http://<IP>:8750/`, também `http://<NOMEDOPC>.local:8750/`), um **QR code** e o
   **código de pareamento** de 6 dígitos com a contagem regressiva. O código vale **5 minutos** e serve para **um**
   celular; depois de usado, vencido ou após muitas tentativas erradas aparece outro (ou clique em **Gerar novo
   código**).
3. No celular: leia o QR code (já leva o código) ou abra o endereço e digite o código. Pronto: o celular fica
   autorizado (um cookie seguro no navegador dele).
4. **Celulares autorizados**: lista com nome, data do pareamento e último acesso. **Remover** tira o acesso daquele
   celular na hora (a página dele fecha a conexão ao vivo e pede o código de novo); **Remover todos** faz o mesmo
   para todos.
5. **Permitir teste pelo celular** (desligado por padrão): libera o botão "Testar" no celular, que pede
   confirmação e toca o sample **na mesa** (pode sair no PA e na transmissão).

### O que o celular pode e não pode

- **Pode:** ver o kit (peças, samples, preset ativo, se o áudio está rodando), trocar o sample de uma peça (só da
  pasta de samples do PC), aplicar um preset inteiro e, se o PC permitir, testar uma peça.
- **Não pode:** enviar arquivos, navegar nas pastas do PC, mudar driver/ASIO, rotas, canais USB, nível da mesa,
  ganho, detecção, ativar/desativar o kit, nem parear outros aparelhos.
- O celular só mostra a troca **depois** que ela foi aplicada (e confirmada pelo áudio, quando ele está rodando;
  com o áudio parado aparece "aplicado; áudio parado"). Se outra pessoa (no PC ou em outro celular) mudou a mesma
  peça antes, o pedido é recusado com "alterado em outro dispositivo" e a tela é atualizada: confira e toque de
  novo. Ao reconectar, o celular recebe o estado atual e **nunca** reenvia pedidos antigos sozinho.
- Um preset com algum arquivo faltando não muda nada; o celular mostra o erro por peça.
- Trocas feitas no celular aparecem na janela do PC na hora e são salvas na configuração como as do PC.

### Rede, firewall e segurança

- Use só em rede local **confiável** (o Wi-Fi da equipe). O painel é HTTP simples, sem HTTPS: alguém com a senha
  do Wi-Fi poderia capturar o acesso de um celular. Por isso o pareamento, a revogação e o fato de o celular só
  poder trocar samples/presets. O app **não** abre portas no roteador, **não** usa UPnP e **não** muda o firewall.
- O PC só aceita pedidos vindos da sub-rede da rede escolhida e do próprio PC, e confere o endereço usado
  (`Host`/`Origin`).
- **Firewall do Windows** (uma vez, num PowerShell **como administrador**; o app não roda isso sozinho). Libera a
  porta só para a sub-rede local e só para o SampleDrum (troque o caminho pelo da pasta do app):

  ```powershell
  New-NetFirewallRule -DisplayName "SampleDrum - painel celular (LAN)" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 8750 -Profile Private -RemoteAddress LocalSubnet `
    -Program "C:\SampleDrum\SampleDrum.App.exe"
  # equivalente netsh:
  netsh advfirewall firewall add rule name="SampleDrum - painel celular (LAN)" dir=in action=allow protocol=TCP localport=8750 profile=private remoteip=localsubnet program="C:\SampleDrum\SampleDrum.App.exe"
  # remover:
  Remove-NetFirewallRule -DisplayName "SampleDrum - painel celular (LAN)"
  ```

  - Sem essa regra, o Windows pode mostrar um aviso do firewall na primeira vez que o painel liga numa rede; se o
    aviso for fechado ou cancelado, o Windows cria uma regra de **bloqueio** para o programa. Para conferir:
    `Get-NetFirewallApplicationFilter | ? Program -like '*SampleDrum*' | Get-NetFirewallRule | ft DisplayName, Action, Profile`.
  - Se a rede da igreja estiver como **Pública** (`Get-NetConnectionProfile`), o preferível é passá-la para Privada
    com o aval de quem cuida da rede (`Set-NetConnectionProfile -InterfaceAlias "<adaptador>" -NetworkCategory Private`);
    a alternativa é uma regra com `-Profile Public -RemoteAddress LocalSubnet`, mais arriscada em rede aberta.
- O celular precisa estar no **mesmo Wi-Fi/sub-rede** do PC. Wi-Fi de convidados ou com isolamento de clientes não
  alcança o PC. Se o IP do PC mudar (DHCP), os celulares precisam parear de novo: peça uma **reserva de IP** no
  roteador ou use o endereço `.local`.

### Simulação

Com `--simulation`, o painel opera o kit e uma **biblioteca de demonstração** (samples e presets de teste numa pasta
temporária, apagada ao fechar). Nada dessa execução (kit, presets, pastas, celulares pareados, opções do painel) é
gravado: o `settings.json` da igreja não é criado nem alterado. Para testar no próprio PC: escolha
"Só este PC (localhost)", **Ativar painel** e abra `http://localhost:8750/` no navegador.

Detalhes da API (rotas, JSON, erros, eventos): [docs/painel-api.md](docs/painel-api.md).

## Peças e padrões de detecção

| Peça | Microfone (mesa) | Padrões iniciais (a calibrar com gravação real) |
|---|---|---|
| Bumbo | CH 21 / AES50 B21 | os da v1: limiar −24 dBFS, passa-altas 40 Hz, intervalo mínimo 35 ms |
| Caixa | CH 19 / AES50 B19 | limiar −20 dBFS, passa-altas 100 Hz (tira o vazamento do bumbo), intervalo 25 ms (rulos/flams), faixa de velocidade 30 dB (ghost notes) |
| Tom | CH 18 / AES50 B18 | limiar −20 dBFS (o microfone ouve o kit inteiro), passa-altas 60 Hz, intervalo 30 ms |
| Surdo | CH 17 / AES50 B17 | limiar −16 dBFS (o bumbo vaza forte no microfone do surdo), passa-altas 50 Hz, release do envelope 20 ms (fundamental grave, perto do bumbo) |

O porquê de cada valor está em [docs/README-dev.md](docs/README-dev.md).

## Perfis de roteamento

A mesa e o X-USB numeram canais a partir de **1**; a API ASIO usa índices a partir de **0**:
`índice ASIO = canal USB − 1`. **Entrada** = mesa → PC (envio USB, entrada ASIO); **saída** = PC → mesa
(retorno USB, saída ASIO).

### Igreja (cena de teste)

| Peça | Entrada (mesa → PC) | Saída (PC → mesa) |
|---|---|---|
| Bumbo | **User Out 3** (fonte CH 21) → USB 3 → **índice ASIO 2** | USB 1 (**índice ASIO 0**) → **Card In 1** → **Aux 1** |
| Caixa | pendente de configuração | pendente de configuração |
| Tom | pendente de configuração | pendente de configuração |
| Surdo | pendente de configuração | pendente de configuração |

- Reservado: **entradas USB 1/2 do PC** = **OBS MixBus 13/14** (mesa → PC). O app nunca as usa como entrada.
  Isso não conflita com o retorno do bumbo, que usa a **saída** USB 1 (PC → mesa): mesmo número, caminho
  físico diferente.
- O bloco **Aux In** foi remapeado para **Card 1-2**, de modo que Card In 1 chega no **Aux 1**. O **Aux 4
  "Celular" não é usado nem alterado**.
- Quando a equipe definir na mesa os envios/retornos da caixa, do tom e do surdo, preencha-os em **Editar
  rotas...** (perfil Igreja).

### Simulação

Quatro pares definidos, para testar sem a mesa: Bumbo entrada USB 3 / saída USB 1, Caixa 4 / 2, Tom 5 / 3,
Surdo 6 / 4 (entradas longe das USB 1/2 do OBS). Na **Simulação (sem ASIO)** as rotas não são usadas; o perfil
serve para validar o roteamento de quatro peças.

### Regra das rotas pendentes e validação

- **Pendente** (entrada e/ou saída sem canal) não é erro: aquela peça **não roda no ASIO** e as outras rodam
  normalmente. A faixa da peça mostra "pendente" e o teste dela não sai na mesa. Se a peça pendente já tem um dos
  canais preenchido e ele repete o canal de outra peça, é reservado ou está fora da faixa, isso aparece só como
  **aviso** nela: não impede as peças completas de iniciar.
- **Erros** (só entre peças com rota completa; o áudio **não inicia** até corrigir; a lista mostra a mensagem):
  - **entrada repetida**: duas peças lendo a mesma entrada USB;
  - **saída repetida**: duas peças escrevendo na mesma saída USB;
  - **canal reservado**: por exemplo entrada USB 1 ou 2 (OBS MixBus 13/14);
  - **fora da faixa**: canal fora de 1..32 (ou além dos canais que o driver informa).
- Entrada e saída são validadas **por direção**: entrada USB 1 e saída USB 1 não conflitam.
- O host ASIO abre o driver **uma vez** com todos os canais das peças roteadas; peças pendentes ficam de fora.

### Configuração da versão 1

Ao abrir, um `settings.json` da v1 é migrado automaticamente: driver, sample e ajustes do bumbo e a rota do bumbo
no perfil Igreja são mantidos; caixa, tom e surdo ficam nos padrões (rotas pendentes). O arquivo original é
copiado para `settings.v1.json` na mesma pasta. Um arquivo corrompido vira padrões (com aviso) e é copiado para
`settings.invalido.json`.

## Primeiro ensaio na igreja (checklist)

Use a **cena de teste** da X32. O OBS fica aberto e capturando normalmente, com a **transmissão pública
DESLIGADA**. Leve a build self-contained e anote tudo que aparecer na linha "Buffer/latência" do app.

1. **Sample chega só no Aux 1**
   - Abra o app, escolha o driver ASIO do X-USB e o perfil **Igreja**. A lista de validação deve mostrar só
     avisos de "pendente" (caixa/tom/surdo); a faixa do Bumbo mostra "Entrada USB 3 (índice ASIO 2) → Saída USB 1
     (índice ASIO 0)".
   - Escolha o WAV do bumbo, clique **Iniciar áudio** e depois **Testar** na faixa do Bumbo.
   - Confirme que o sample aparece **só no Aux 1** (medidores da mesa) e em nenhum outro canal ou bus.
2. **OBS continua igual**
   - Com o app rodando, confirme que o áudio dos buses 13/14 continua chegando ao OBS **sem mudança**
     (nível, sem cortes, sem atraso novo, sem o sample no OBS a menos que a equipe o mande para lá).
3. **Disparo automático**
   - Clique **Ativar kit** e peça ao baterista para tocar suave e forte, golpes isolados e duplas rápidas.
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
4. **Gravar as peças cruas para calibração**
   - Grave alguns minutos da entrada USB 3 (bumbo cru, suave e forte, com a banda tocando para ter o
     vazamento real) e, se já houver envio, dos microfones da caixa, do tom e do surdo. Esses arquivos calibram
     limiar, faixa de velocidade e intervalo mínimo de cada peça no modo offline (`--mic arquivo.wav`), sem a mesa.
5. **Caixa, tom e surdo** (quando a equipe definir os envios/retornos na mesa)
   - Preencha as rotas em **Editar rotas...** (perfil Igreja), confira a lista de validação (sem erros) e repita
     os passos 1-3 para cada peça, confirmando que cada sample chega **só** no retorno daquela peça.

## Depende do ensaio presencial

Ainda não confirmado (sem hardware até agora):

- Versão do driver ASIO do X-USB e o formato nativo das amostras (esperado `Int32LSB`; o app aceita também
  `Float32LSB`, `Int16LSB` e `Int24LSB`, com um formato único por lado).
- Tipo de fonte de áudio usada no OBS (ASIO, WASAPI/WDM) e se o driver X-USB aceita o app e o OBS ao mesmo
  tempo (driver compartilhado).
- Latência real ida e volta (buffer configurado no painel do driver + conversões + janela de disparo
  `max(DetectTimeMs, ScanMs)`, 2 ms por padrão; ajustável em **Avançado → Janela de velocidade**).
- Níveis reais de cada peça e do vazamento entre microfones, e portanto os padrões finais de limiar, velocidade,
  intervalo mínimo e redisparo de cada peça.
- **Surdo: limiar padrão −16 dBFS é um compromisso.** Ele foi elevado de −22 para que o vazamento forte do bumbo
  (≈ −21 dBFS no microfone do surdo, no cenário sintético) não dispare o surdo; em troca, golpes **muito suaves** do
  surdo, cujo pico no caminho de detecção fique abaixo de ≈ −16 dBFS, deixam de disparar. Com a gravação da igreja,
  meça o vazamento do bumbo e o golpe mais suave do surdo e ponha o limiar entre os dois (mais perto do golpe se o
  vazamento for baixo). Golpes suaves do surdo **perto do limiar** disparam cerca de **5 ms depois** dos fortes (o
  detector espera a primeira crista da nota grave; medido no cenário sintético, a reconferir com a gravação real).
- Envios e retornos USB da caixa, do tom e do surdo na mesa (hoje "pendentes").

## Documentação

- [docs/README-dev.md](docs/README-dev.md) — detector, padrões por peça, modo offline e testes.
- [docs/asio-naudio.md](docs/asio-naudio.md) — ASIO com NAudio, threads, formatos e índices.
- [docs/research/lsp-trigger.md](docs/research/lsp-trigger.md) — pesquisa sobre o LSP Trigger (referência
  de comportamento; nenhum código do LSP, que é LGPL-3, foi copiado).
- [docs/painel-api.md](docs/painel-api.md) — contrato da API do painel do celular (rotas, JSON, erros, eventos SSE,
  pareamento, ordem dos pedidos).
- [docs/research/painel-movel.md](docs/research/painel-movel.md) — pesquisa e plano do painel do celular (com o
  estado do que foi entregue).
