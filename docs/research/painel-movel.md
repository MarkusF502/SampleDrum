# Pesquisa e plano: painel de controle pelo celular (SampleDrum v2 → próxima entrega)

Data: 2026-11-25; atualizado em 2026-11-26 para o Core em `ao/sampledrum-core/root` @ `3a4efd5` (seções 5 a 9). Escopo: **só pesquisa e plano**, sem implementação do servidor nem da página. Etiquetas: **[fonte]** = documentação ou página citada; **[observação]** = medido ou testado neste PC de desenvolvimento; **[inferência]** = conclusão nossa, a validar.

> **Estado em 2026-11-27 (v3): implementado.** Servidor `src/SampleDrum.Remote` (Kestrel no processo, SSE + POST
> JSON, página embutida), grupo **Painel do celular** na janela do PC, contrato em [../painel-api.md](../painel-api.md)
> e uso no [README](../../README.md#painel-do-celular). Diferenças em relação ao plano abaixo:
> - `baseVersion` velho **bloqueia** o pedido (`409 stale_state` com o estado atual) em vez de só marcar `conflict`:
>   uma seleção antiga nunca sobrescreve uma mais nova; o operador confirma e toca de novo.
> - O app **não** tem o botão "Liberar painel no firewall" (`--setup-firewall`) nem detecta o perfil da rede: os
>   comandos ficam documentados no README e o app nunca muda o firewall sozinho.
> - O celular **não** ativa/desativa o kit nem muda ganho nesta entrega (perguntas 3 e 5 da seção 8 em aberto).
> - Limites: pareamento 5 erros/min por IP e 20/min no total (código invalidado após 10 erros), 5 comandos aceitos
>   por segundo por aparelho, 4 conexões SSE por aparelho (32 no total), até 16 aparelhos pareados.
> - Mudança de IP (DHCP): o PC avisa no log e pede para reativar na rede certa (não reinicia sozinho).
> - `--simulation` usa uma biblioteca de demonstração temporária e **não grava** o `settings.json`.
> - Validado: compilado, testes automáticos (API em memória, Kestrel real, sessão `--simulation`) e `localhost`
>   com o app em `--simulation`. **Pendente:** celular real no Wi-Fi (seção 7.3) e checklist na igreja (seção 7.4).

**Objetivo:** pelo navegador do celular, na mesma rede do PC da transmissão, o operador escolhe um sample por peça ou aplica um preset de kit inteiro com o app rodando. O áudio continua sendo processado só no PC e **nunca depende da rede**.

## Resumo da recomendação

- **Servidor:** Kestrel (ASP.NET Core minimal APIs) **dentro do processo** do app, em um projeto novo `SampleDrum.Remote`.
- **Rede:** ouve só no(s) IP(s) da LAN e no loopback, porta fixa **8750** (configurável).
- **Comunicação:** **SSE** para o estado ao vivo e **POST JSON** para os comandos. Página HTML/JS sem framework, embutida no exe.
- **Acesso:** **HTTP + pareamento** (código de 6 dígitos ou QR na tela do PC), token de aparelho em cookie `HttpOnly; SameSite=Strict`, checagem de Origin/Host, limite de tentativas e lista de aparelhos revogáveis no PC.
- **Lógica:** um **`KitController` único** (fonte única de verdade, com número de versão do estado) serializa as trocas. O PC e o celular usam o mesmo caminho: `SampleLibrary.PrepareAsync`, depois `KitEngine.ApplyKit`, que troca atomicamente na fronteira de bloco.

## 1. Como hospedar o painel

| Opção | Custo | Prós | Contras |
|---|---|---|---|
| **Kestrel in-process** (`<FrameworkReference Include="Microsoft.AspNetCore.App" />` no projeto WinForms) | Self-contained **118 → 145 MB (+27 MB, +139 arquivos)**; zip **48 → 59 MB** [observação: projetos de teste publicados com `-r win-x64 --self-contained`, .NET SDK 10.0.401]. Uma build *framework-dependent* passaria a exigir **também** o ASP.NET Core Runtime no PC (o `runtimeconfig.json` lista NETCore + WindowsDesktop + AspNetCore) [observação] | Roteamento, JSON, SSE nativo (`TypedResults.ServerSentEvents`, .NET 10), WebSocket, rate limiting, arquivos estáticos e **testes em memória** (`TestServer`/`WebApplicationFactory`), tudo no framework compartilhado e sem pacote de terceiros [fonte] | +27 MB na pasta publicada |
| **HttpListener** (http.sys) | 0 MB | Embutido; também faz WebSocket | Prefixo LAN sem admin falha: `http://+:18731/` e `http://<IP-da-LAN>:18731/` dão **"Acesso negado"**; `http://localhost:18731/` funciona [observação, usuário sem admin]. Exige `netsh http add urlacl url=http://+:8750/ user=<usuário>` uma vez, como admin [fonte]. Sem servidor de teste em memória; roteamento, JSON, SSE e limites são todos manuais |
| **EmbedIO** | pequeno | MIT | Repositório **arquivado**; última versão 3.5.2 (out/2022) [observação: NuGet + GitHub]. **Descartado** |
| **Processo companheiro** (exe web separado) | +1 processo | Isola falhas do áudio | Precisa de IPC para o estado e os comandos; dois processos para instalar, iniciar e manter versionados; duplica a fonte de verdade. Ganho de isolamento pequeno, porque o áudio já roda em thread própria e sem lock [inferência] |

**Como o estado chega ao celular:**
- **Polling:** simples, mas tem latência ou gasta bateria.
- **SSE (`EventSource`):** só servidor → celular, HTTP/1.1 comum, **reconexão automática** e `Last-Event-ID` no navegador [fonte MDN]. Tem suporte nativo no .NET 10 [fonte].
- **WebSocket:** bidirecional, mas exige implementar reconexão, ping e protocolo à mão.
- **SignalR:** o servidor está no framework, mas é preciso embutir o cliente JS e a complexidade do protocolo de hub não compensa aqui.

Os comandos são raros (trocar sample, aplicar preset), então **POST + SSE** basta [inferência].

**Recomendação: Kestrel in-process + SSE.**
- Os +27 MB são aceitáveis numa pasta que já tem ~120 MB, e a build self-contained continua sem exigir instalação.
- Ganhamos testes automatizados de API em memória e middlewares prontos (rate limit, Origin, arquivos estáticos).
- O Kestrel usa sockets, não http.sys, então ouvir num IP da LAN **não precisa de urlacl** [inferência; a regra de firewall continua necessária, seção 2].
- Os arquivos estáticos (HTML/CSS/JS < 50 KB, sem etapa de build) vão como `EmbeddedResource`. Sem internet e sem CDN: nada é baixado em tempo de uso.

## 2. Descoberta e abertura no celular

- **URL e porta:** porta fixa **8750**. Se estiver ocupada, o app avisa e o painel fica desligado; o áudio segue normal. O PC mostra o **endereço** (`http://<IP>:8750/`) e um **QR code**.
  - Para o QR: **Net.Codecrete.QrCodeGenerator** (MIT, 3.2.1 de set/2026, gera só a matriz e o app desenha no WinForms) ou **QRCoder** (MIT, 1.9.0 de nov/2026) [observação: NuGet].
- **IP ou nome:** o IPv4 é a opção primária. O nome `http://<NOMEDOPC>.local:8750/` fica como secundário:
  - iOS resolve `.local` nativamente (Bonjour) [fonte: Wikipedia .local].
  - O resolvedor do Android faz mDNS `.local` desde o fim de 2021 (Android 12+), mas com relatos de falha por fabricante; não vale em VPN nem em dados móveis [fonte: AOSP, Esper].
  - O Windows 10 1903+ e o 11 respondem por `NOMEDOPC.local` [fonte: Microsoft Networking Blog, Wikipedia], salvo política corporativa ou firewall.
- **Escolha das interfaces:** só adaptadores ativos com IPv4 privado (10/8, 172.16/12, 192.168/16), excluindo loopback, 169.254.x, `vEthernet` (WSL/Hyper-V) e VPN. Este PC de desenvolvimento, por exemplo, tem Ethernet + `vEthernet (WSL)` [observação]. Se houver mais de um candidato, a tela lista todos.
- **IP via DHCP:** ouvir em `NetworkChange.NetworkAddressChanged` e reiniciar o host web nos IPs novos (o QR se atualiza). **Atenção:** o cookie de pareamento é preso à *origem* (IP:porta), então um IP novo faz o celular **parear de novo** [inferência]. Por isso: pedir ao responsável pela rede uma **reserva DHCP** para o PC (pergunta em aberto), ou usar a URL `.local`, cuja origem é estável.
- **Firewall do Windows** (uma vez, com consentimento de admin). O app oferece o botão "Liberar painel no firewall", que roda `SampleDrum.App.exe --setup-firewall` com elevação (UAC):

  ```powershell
  New-NetFirewallRule -DisplayName "SampleDrum - painel celular (LAN)" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 8750 -Profile Private -RemoteAddress LocalSubnet `
    -Program "C:\SampleDrum\SampleDrum.App.exe"
  # equivalente netsh:
  netsh advfirewall firewall add rule name="SampleDrum - painel celular (LAN)" dir=in action=allow protocol=TCP localport=8750 profile=private remoteip=localsubnet program="C:\SampleDrum\SampleDrum.App.exe"
  # remover:
  Remove-NetFirewallRule -DisplayName "SampleDrum - painel celular (LAN)"
  ```
  [fonte: New-NetFirewallRule]. Com uma regra explícita para o programa, o pop-up do firewall ao ouvir na rede não deve aparecer no meio do culto [inferência].
- **Rede como "Pública":** a rede deste PC de desenvolvimento está como **Public** [observação: `Get-NetConnectionProfile`]. Se a da igreja também estiver:
  - Preferível: mudar a rede para Privada (`Set-NetConnectionProfile -InterfaceAlias "<adaptador>" -NetworkCategory Private`, admin, vale para o PC inteiro) [fonte], com aval de quem cuida da rede.
  - Alternativa: uma segunda regra com `-Profile Public -RemoteAddress LocalSubnet`, mais arriscada em rede aberta.
  - O app detecta o perfil e explica qual das duas aplicar. **Nunca muda o perfil sozinho.**
- **Celular em outra sub-rede ou com isolamento de clientes** (Wi-Fi de convidados, AP com *client isolation*, Wi-Fi roteado em VLAN própria): o celular não alcança o PC, ou `LocalSubnet` o bloqueia.
  - Saídas: usar o Wi-Fi da equipe (não o de convidados); pedir a faixa do Wi-Fi para pôr em `-RemoteAddress`; ou, em último caso, um AP dedicado barato ligado à mesma LAN do PC.
  - O hotspot do Windows não é recomendado: compete com o Wi-Fi do PC e exige configuração toda vez [inferência].

## 3. Controle de acesso

**Ameaça realista:** alguém no Wi-Fi da igreja (com a senha) abre o painel e troca samples ou desativa o kit no meio do culto. Não há dado sensível. O risco é operacional.

- **Só a rede local:**
  - Firewall `LocalSubnet` (seção 2).
  - O app recusa IPs remotos fora das sub-redes das interfaces escolhidas (defesa em profundidade).
  - Checagem do cabeçalho `Host`: só aceita `IP:8750`, `NOMEDOPC.local:8750` e `localhost`. Isso bloqueia *DNS rebinding* [inferência].
- **Pareamento (primeira conexão):** o PC mostra, num quadro **não modal**, um código de 6 dígitos válido por 5 minutos, também embutido no QR (`http://IP:8750/#pair=123456`).
  - Um "Aprovar este celular?" modal roubaria o foco do operador do OBS durante o culto, por isso **não** é recomendado.
  - O celular envia `POST /api/pair {code, deviceName}` e recebe um token aleatório de 256 bits num **cookie `HttpOnly; SameSite=Strict; Path=/`** (o `EventSource` envia cookies sozinho; cabeçalho customizado não dá) [fonte MDN].
  - O PC guarda só o **hash SHA-256** do token, com nome do aparelho, data de pareamento e último acesso.
- **Revogação:** lista "Celulares autorizados" no PC com um botão "Remover"; o efeito é imediato e a conexão SSE desse aparelho é fechada. Há também a opção "Remover todos".
- **CSRF:** cookie `SameSite=Strict`, `Origin` obrigatório e igual a um host permitido em todo POST, e corpo `application/json` obrigatório (POST cruzado exige *preflight*, que o servidor nega) [fonte OWASP].
- **Limite de tentativas** (rate limiting nativo do ASP.NET Core) [fonte]:
  - `/api/pair`: 5 tentativas por minuto por IP; após 10 erros, o código é invalidado e o PC gera outro.
  - Comandos: 5 por segundo por aparelho.
  - `/api/events`: no máximo 4 conexões por aparelho.
- **HTTPS ou HTTP:**
  - Na LAN não há certificado público (sem domínio nem internet). Certificado autoassinado causa aviso em todo acesso; no iOS é preciso instalar e confiar num perfil, e o Android não confia em CA de usuário para tudo. Isso é inviável para operadores voluntários [inferência].
  - **Proposta:** HTTP + pareamento. O risco residual é alguém **com a senha do Wi-Fi** capturar o token (WPA2-PSK). Mitigação: revogação e o fato de o impacto ser só operacional.
  - Para o futuro, se for preciso: HTTPS com CA própria instalada uma vez nos celulares da equipe.

## 4. API (rascunho)

Todas as respostas são JSON `camelCase`, e os erros seguem `application/problem+json` (`{type, title, status, detail, code}`).

| Método e rota | Uso |
|---|---|
| `GET /` | Página do painel (embutida) |
| `POST /api/pair` `{code, deviceName}` | Pareamento → `204` + cookie; `401 pairing_invalid` / `429` |
| `GET /api/state` | Retrato completo do estado (abaixo) |
| `GET /api/events` | SSE: `state` (retrato completo na conexão e a cada mudança), `meters` (~5 Hz), `hit`, `op` |
| `GET /api/samples` | Biblioteca: `[{id, name, durationMs, sampleRate, channels}]`. O `id` é o **caminho relativo** à pasta de samples; o celular nunca envia caminho absoluto, e o servidor resolve dentro da raiz e rejeita `..` |
| `GET /api/presets` | `[{id, name, pieces: {kick: {sampleId, gainDb}, ...}}]` |
| `POST /api/kit/preset` `{presetId, clientOpId, baseVersion}` | Aplica o kit inteiro → `202 {opId}` |
| `POST /api/pieces/{piece}/sample` `{sampleId, clientOpId, baseVersion}` | Troca uma peça → `202 {opId}` |
| `POST /api/pieces/{piece}/test` `{gain}` | Teste manual (ver política abaixo) → `202` ou `403 test_not_allowed` |
| `POST /api/kit/enabled` `{enabled}` | Ativar/Desativar o kit (ver perguntas abertas) |
| `GET /api/operations/{opId}` | `{opId, clientOpId, kind, status: queued\|preparing\|applied\|failed\|superseded, errors: [{piece, code, message}], resultVersion}` |

Exemplo de `state`:

```json
{ "version": 42, "kitEnabled": true, "profile": "Igreja",
  "kit": { "name": "Rock 1", "presetId": "rock-1", "appliedAt": "2026-11-25T19:02:11Z" },
  "pieces": [ { "piece": "kick", "label": "Bumbo", "enabled": true, "route": "ok",
                "sample": { "id": "bumbos/kick-seco.wav", "name": "kick-seco", "durationMs": 420 },
                "outputGainDb": 0, "lastHit": { "at": "2026-11-25T19:05:01.120Z", "velocity": 0.71 } },
              { "piece": "snare", "label": "Caixa", "enabled": false, "route": "pending", "sample": null } ],
  "pendingOperation": null, "allowPhoneTest": false }
```

- `meters`: `{ "kick": -18.5, "snare": -90, ... }` em dBFS, ~5 Hz. Mandar o medidor a 30 Hz pela rede não acrescenta nada no celular [inferência].
- **Idempotência:** o celular gera `clientOpId` (UUID). Se a rede cair e o POST for repetido com o mesmo id, ele devolve a mesma operação em vez de enfileirar outra.
- **Teste pelo celular:** o teste toca o sample **na mesa**, então pode sair no PA ou na transmissão. Proposta: **desligado por padrão** e habilitado no PC ("Permitir teste pelo celular"); no celular, botão com confirmação.

## 5. Semântica da troca, de ponta a ponta (como o Core faz em `3a4efd5`)

1. O comando (do PC ou do celular) entra no **`KitController`**: uma fila serial (`Channel`, um único worker) **fora** da thread de áudio. Cada chamada devolve um `KitOperation` com `Id` (Guid), `Snapshot` (status `Queued → Preparing → Applied | Failed | Superseded`, erros, `ResultVersion`) e `Completion` (resolve sempre, inclusive em falha) [fonte: `Kit/KitController.cs`].
2. **Preset inteiro** (`ApplyPreset`): `SampleLibrary.PrepareAsync` carrega, reamostra e sanitiza **todas** as peças. Se **alguma** falhar, a operação vira `Failed` com o erro por peça e **o kit ativo não é tocado** [fonte; observação: probe da revisão].
3. **Uma peça** (`SetPieceSample(id)` para o celular, `SetPieceSamplePath(path)` só para o seletor do PC): prepara só aquela peça e reaproveita os arrays das outras (`PreparePieceAsync` + `PreparedKit.WithPiece`). Uma troca de peça zera o `PresetId` (vira seleção personalizada).
4. `KitEngine.ApplyKit` faz **uma troca de referência**, que vale no **início do próximo bloco**. Os golpes seguintes usam o sample novo e as vozes que já tocam terminam com o antigo. Sample rate diferente → `Failed`, motor intacto. `Applied` quer dizer "entregue ao motor" (também com o áudio parado); a confirmação de que a thread de áudio pegou a troca é `KitEngine.AppliedKitGeneration`. `Reset()` aplica um kit pendente. Ganhos e detecção do preset entram **por cima da calibração atual**, e duas trocas antes de um bloco não perdem ganhos [fonte; observação: re-check final].
5. Cada operação aplicada incrementa `Version` uma vez e dispara `StateChanged` (sem argumentos, na thread do worker, fora de locks); quem escuta lê `Snapshot` (`KitStateSnapshot`: `Version`, `KitEnabled`, `ActivePresetId`, por peça `SampleId`, `SamplePath`, ganho, `Enabled`, `Settings`).
6. **Concorrência:** fila serial; o *supersede* é **direcional**: troca de kit inteiro cancela trocas de kit e de peça mais antigas; troca de peça cancela só trocas antigas da **mesma** peça (nunca um preset pendente); habilitar/ganho/ajustes/kit-ativo cancelam o mesmo tipo para o mesmo alvo. O cancelado nunca chega ao motor, mesmo se já estava preparando.
   - `baseVersion` e `clientOpId` **não existem no Core**: ficam no adaptador web (seção 9). `baseVersion` não bloqueia; se estiver velho, a resposta leva `conflict: true` e o celular mostra "alterado por outro dispositivo" [inferência: travar seria pior ao vivo].
7. **Pedidos inválidos** (ajustes sem `Trigger`, ganho não finito, id fora da biblioteca) viram `Failed` com código estável (`InvalidArgument`, `OutsideLibrary`); exceção inesperada vira `Failed`/`Internal` e o worker continua [observação: re-check final].
8. **Partida do app:** o kit salvo volta como **uma** operação de kit inteiro só com samples (`AppConfig.ToStartupPreset`), mantendo o `ActivePresetId` se as peças ainda batem com o arquivo do preset, **antes** de o áudio começar. O kit continua começando **desativado**.
9. **Memória e GC:** um kit de 4 samples de ~1 s ocupa ~0,8 MB, que vai para o LOH. Kits antigos ficam para o GC quando as vozes terminam. **Risco:** pausa de GC gen2 durante uma troca. Medir com o harness; mitigação possível: `GCSettings.LatencyMode = SustainedLowLatency` enquanto o áudio roda [inferência].

## 6. Sincronização PC ↔ celular e queda do Wi-Fi

- **Fonte única de verdade:** um único `KitController` durante toda a execução (a pasta de samples muda por `SetLibraryFolder`, sem recriar o controlador, então `Version` nunca volta). A UI do PC já passa por ele para samples, presets, ajustes e ativações; só `RequestTest` e `Reset` (com o áudio parado) tocam o motor direto. `StateChanged` chega na thread do worker: a UI precisa repassar com `BeginInvoke` e nenhum assinante pode bloquear esperando a UI.
- **SSE:** na conexão o servidor manda primeiro o `state` completo, com `id: <version>`; depois manda `state` a cada mudança. Na reconexão o navegador reenvia `Last-Event-ID`, e o servidor sempre responde com o retrato completo, sem replay de eventos. É simples e não perde nada [inferência].
- **Medidor e golpes:** `ReadAndResetInputPeak` e `TryDequeueEvent` são de **consumidor único**. O app tem um único *sampler* (30 Hz) que lê o motor e distribui para a UI do PC e para o hub web. O hub web nunca chama o motor.
- **Wi-Fi cai:**
  - O áudio não percebe: o Kestrel roda no thread pool e nunca bloqueia o callback ASIO, e o último kit aplicado continua tocando.
  - O celular mostra a faixa **"Sem conexão com o PC"**, desabilita os botões e reconecta sozinho (EventSource). Ao voltar, recebe o retrato completo.
  - Uma operação que estava em andamento termina no PC de forma atômica (aplicada inteira ou não aplicada). O celular consulta `GET /api/operations/{opId}`.
- **Falhas do servidor web** (porta ocupada, exceção) aparecem no log do PC e desligam só o painel.

## 7. Plano de testes

1. **Automáticos sem rede (CI local):**
   - Core: **já coberto** em `3a4efd5` (`tests/SampleDrum.Core.Tests/Panel*Tests.cs`: controlador, biblioteca, config, motor, robustez). Falta só o que o adaptador acrescentar.
   - API com `WebApplicationFactory`/`TestServer` [fonte]: pareamento (código certo, errado, expirado, rate limit), Origin/Host inválidos dão 403, cookie revogado dá 401, `sampleId` com `..` dá 400, idempotência por `clientOpId`, SSE envia o `state` inicial e o próximo `version`.
   - Teste de troca de kit **durante o processamento** em blocos de 32 a 1024: as vozes antigas terminam, a troca acontece no início de um bloco e não há alocação no `Process` (já coberto pelo padrão do Core).
2. **Localhost com o harness** (sem mesa): `SampleDrum.App.exe --simulation` abre no perfil Simulação com o kit sintético e **não cria nem altera** o `settings.json` do operador (`AppConfig.SaveSimulationRun`). Abrir `http://localhost:8750` no navegador do PC, trocar presets e peças durante a bateria simulada e conferir eventos, `Version` e ausência de *glitch* no medidor de saída. Antes e depois: `SampleDrum.App.exe --selftest` e `SampleDrum.App.exe --harness <pasta>` (código de saída 0 = ok; ver `docs/harness.md` para o PowerShell).
3. **Celular no mesmo Wi-Fi (em casa):**
   - Regra de firewall criada com o botão de admin, abertura pelo QR e pareamento.
   - Trocar uma peça e um preset; dois celulares ao mesmo tempo; PC e celular ao mesmo tempo.
   - Desligar o Wi-Fi do celular no meio de uma troca: o PC termina de forma atômica e o celular volta sincronizado.
   - Revogar um aparelho; mudar o IP (renovar o DHCP) e conferir o aviso de "parear de novo".
   - `.local` no iPhone e no Android.
4. **Checklist na igreja (precisa do hardware):**
   - Perfil da rede (Privada?), sub-rede do Wi-Fi da equipe, isolamento de clientes e reserva DHCP.
   - Teste com o OBS transmitindo (CPU, sem *dropout*) e troca de preset entre músicas.
   - Confirmar que o teste pelo celular está desligado por padrão e que o Aux 4 "Celular" e o caminho do OBS (USB 1/2) não foram tocados.

## 8. Plano da próxima entrega

| Marco | Conteúdo | Estimativa |
|---|---|---|
| ~~M1 Core~~ | **Feito em `3a4efd5`** (`KitController`, ids da biblioteca, códigos de erro, ids de preset, `AppliedKitGeneration`, esquema `RemotePanel`) | 0 |
| M1 App: estado e distribuição | `MainForm` assina `StateChanged` (repasse à UI, painéis atualizados pelo `Snapshot`), salva por `AppConfig.Save(path, snapshot)` numa única thread, e um *hub* único lê medidores/eventos do motor e distribui para a UI e para o painel (seção 9) | 0,5–1 dia |
| M2 `src/SampleDrum.Remote` (net10.0, FrameworkReference AspNetCore) | Host Kestrel inicia/para pelo app, endpoints sobre o `KitController`, SSE, pareamento e tokens (hash em `RemotePanel.PairedDevices`), `clientOpId`/`baseVersion`, Origin/Host, rate limit, arquivos embutidos; `tests/SampleDrum.Remote.Tests` com `Microsoft.AspNetCore.Mvc.Testing` | 2 dias |
| M3 Página do celular | `wwwroot/` (HTML/CSS/JS puro, pt-BR, botões grandes, modo escuro, faixa offline, confirmações) | 1–1,5 dia |
| M4 Integração na UI do PC | Seção "Painel do celular" (liga/desliga, URL + QR, código de pareamento, aparelhos autorizados, "Permitir teste pelo celular", botão de firewall `--setup-firewall` elevado, aviso de rede Pública) | 1 dia |
| M5 Validação | Localhost com `--simulation`, celulares reais, documentação de uso | 0,5–1 dia |

**Total ≈ 5 a 6,5 dias de worker** (antes 5,5 a 6,5; o Core saiu do plano, mas a integração do estado no app entrou).

**Riscos:**
- Topologia do Wi-Fi da igreja (isolamento ou sub-rede diferente).
- Rede como Pública.
- Falta de acesso de admin para criar a regra de firewall.
- Pausa de GC na troca (medir).
- +27 MB na build.

**Perguntas para o usuário:**
1. O Wi-Fi que os celulares da equipe usam é o mesmo da rede do PC (mesma sub-rede, sem isolamento)? Dá para reservar o IP do PC no roteador?
2. Há acesso de administrador no PC para criar a regra de firewall uma vez?
3. O celular pode **ativar/desativar o kit inteiro**? (Proposta: sim, com confirmação.) E **testar samples**? (Proposta: só se habilitado no PC.)
4. Quantos celulares devem ser autorizados ao mesmo tempo?
5. O celular também deve ajustar o **ganho por peça**, ou só escolher samples e presets nesta entrega?

## 9. Core (`3a4efd5`): o que já existe e o que falta para o painel

**O que já existe no Core (`3a4efd5`)** [fonte: `src/SampleDrum.Core/Kit/*`; observação: revisões e re-check final]:
- **`KitController`** (único, vive a execução inteira): `SetPieceSample(id)` (celular), `SetPieceSamplePath(path)` (só PC), `ApplyPreset`, `ApplyKit(kit, settings)`, `SetPieceEnabled`, `SetPieceGain`, `SetPieceSettings`, `SetKitEnabled`, `SetLibraryFolder`. Cada chamada devolve `KitOperation` (`Id`, `Snapshot`, `Completion`); `GetOperation(id)` guarda as últimas `OperationHistory` = 256; `Version`, `Snapshot` (`KitStateSnapshot`) e `StateChanged`. *Supersede* direcional (seção 5).
- **Códigos de erro** `PieceLoadErrorCode`: `FileNotFound`, `UnsupportedFormat`, `TooLong`, `OutsideLibrary`, `IoError`, `InvalidArgument`, `Internal`, além da mensagem pt-BR. Pedido inválido ou exceção vira `Failed`; o worker não morre.
- **`SampleLibrary`**: `List()` devolve `SampleInfo.Id` (caminho relativo com `/`, pula links simbólicos/junções); `TryResolveInLibrary(id, out path)` rejeita `..`, caminhos absolutos, UNC, `\\?\`, `C:arquivo`, `:ads`, nomes de dispositivo (`CON`, `NUL`, `COM0-9`, `LPT0-9`, `¹²³`) e qualquer link no caminho. `ResolvePath` continua só para o PC.
- **Presets**: `KitPreset.Id` = nome do arquivo sem `.json` (`Load`/`LoadFolder`); `PreparedKit.PresetId` vai para `Snapshot.ActivePresetId` e é zerado numa troca de peça.
- **Motor**: `ApplyKit` (taxa errada lança; ganhos/detecção por cima da calibração atual; duas trocas antes de um bloco não perdem ganhos), `ActiveKit` = último kit entregue, `AppliedKitGeneration` = troca já pega pela thread de áudio, `Reset` aplica o pendente.
- **`AppConfig`**: `RemotePanel` (`Enabled` falso, `Port` 8750, `AllowPhoneTest` falso, `PairedDevices` com `TokenSha256Hex`), `ActivePresetId`, `SamplePath` relativo à pasta de samples quando está dentro dela, gravação atômica (arquivo temporário único + `Move`), `Save(path, snapshot)` e `SaveSimulationRun` (o `--simulation` não cria nem muda a configuração do operador); `ToStartupPreset` restaura o kit e o preset ativo na partida.

**O que falta para o painel** (fora do Core, próxima entrega):
- **App ouvir o controlador:** hoje o `MainForm` só reage ao `Completion` das operações que ele mesmo fez; uma troca vinda do celular **não** atualizaria os painéis do PC nem seria salva. Assinar `StateChanged`, repassar à UI e persistir por `AppConfig.Save(path, snapshot)` numa única thread (marco M1).
- **Hub de medidores e golpes:** `ReadAndResetInputPeak` e `TryDequeueEvent` continuam de consumidor único (documentado); o app precisa de um leitor único que distribua para a UI e para o SSE.
- **Adaptador web** (`SampleDrum.Remote`): mapear `clientOpId` → `KitOperation.Id` (idempotência), calcular `conflict` a partir de `baseVersion`, traduzir `PieceLoadErrorCode` para `problem+json`, procurar o preset pelo `Id` na lista de `LoadFolder` (nunca montar caminho com o texto do celular), e usar só `SetPieceSample(id)`.
- **Teste pelo celular:** não passa pelo controlador (é `KitEngine.RequestTest`); o adaptador precisa checar `RemotePanel.AllowPhoneTest` antes.
- **Pareamento e tokens:** só o esquema existe; faltam geração do código, hash/validação do token, `LastSeenUtc`, revogação e limite de tentativas.
- **Dados de exibição:** nome e duração do sample saem de `ActiveKit[peça]` (`Name`, `Samples.Length`); o último golpe e o medidor vêm do hub, não do `Snapshot`.

## Fontes

- ASP.NET Core em app não web / FrameworkReference: https://learn.microsoft.com/aspnet/core/fundamentals/target-aspnetcore
- Endpoints do Kestrel: https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints
- `TypedResults.ServerSentEvents` (.NET 10): https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.typedresults.serversentevents
- Rate limiting: https://learn.microsoft.com/aspnet/core/performance/rate-limit
- Testes de integração (TestServer): https://learn.microsoft.com/aspnet/core/test/integration-tests
- `netsh http add urlacl`: https://learn.microsoft.com/windows/win32/http/add-urlacl
- `New-NetFirewallRule`: https://learn.microsoft.com/powershell/module/netsecurity/new-netfirewallrule
- `Set-NetConnectionProfile`: https://learn.microsoft.com/powershell/module/netconnection/set-netconnectionprofile
- EmbedIO (arquivado): https://github.com/unosquare/embedio
- QRCoder: https://github.com/codebude/QRCoder · Net.Codecrete.QrCodeGenerator: https://github.com/manuelbl/QrCodeGenerator
- mDNS no resolvedor do Android: https://source.android.com/docs/core/architecture/modular-system/dns-resolver · https://www.esper.io/blog/android-dessert-bites-26-mdns-local-47912385
- mDNS no Windows: https://techcommunity.microsoft.com/blog/networkingblog/mdns-in-the-enterprise/3275777 · https://en.wikipedia.org/wiki/.local
- EventSource / SSE: https://developer.mozilla.org/docs/Web/API/EventSource
- Cookies SameSite: https://developer.mozilla.org/docs/Web/HTTP/Headers/Set-Cookie
- CSRF: https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html
