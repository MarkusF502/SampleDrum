# Pesquisa e plano: painel de controle pelo celular (SampleDrum v2 → próxima entrega)

Data: 2026-11-25. Escopo: **só pesquisa e plano**, sem implementação. Etiquetas: **[fonte]** = documentação ou página citada; **[observação]** = medido ou testado neste PC de desenvolvimento; **[inferência]** = conclusão nossa, a validar.

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

## 5. Semântica da troca, de ponta a ponta (sobre o contrato Core v2)

1. O comando (do PC ou do celular) entra no **`KitController`**: uma fila única (`Channel<T>`, um consumidor) **fora** da thread de áudio.
2. **Preset inteiro:** `SampleLibrary.PrepareAsync(preset, 48000, ct)` carrega, reamostra e sanitiza **todas** as peças. Se **alguma** peça falhar, a operação inteira vira `failed` (com o erro por peça) e **o kit ativo não é tocado**. Kit pela metade nunca existe.
3. **Uma peça:** prepara só aquela peça e monta um `PreparedKit` novo **reaproveitando os arrays já preparados** das outras peças do kit ativo (sugestão de API na seção 9).
4. `KitEngine.ApplyKit(prepared)` faz **uma troca de referência**, que vale no **início do próximo bloco**. Os golpes seguintes usam o sample novo; as vozes que já estão tocando terminam com o sample antigo (contrato). Até esse momento, o áudio segue com o kit anterior.
5. Em seguida: `version++`, o `AppConfig` é salvo (escrita atômica tmp + move, com sample e ganho por peça e o nome do preset ativo), e o novo estado é transmitido por SSE e para a UI do PC.
6. **Concorrência:** fila serial, **o último a escrever vence**, na ordem de chegada. Se chegar um pedido mais novo para o mesmo escopo enquanto outro ainda prepara, o anterior termina como `superseded` (preparado, mas não aplicado), para evitar duas trocas audíveis seguidas.
   - `baseVersion` não bloqueia. Se estiver velho, a operação ainda é aplicada, mas o resultado vem marcado como `conflict: true` e o celular mostra "alterado por outro dispositivo" [inferência: travar seria pior ao vivo].
7. **Partida do app:** o kit salvo é preparado e aplicado **antes** de o áudio começar. O kit continua começando **desativado** (contrato).
8. **Memória e GC:** um kit de 4 samples de ~1 s ocupa ~0,8 MB, que vai para o LOH. Kits antigos ficam para o GC quando as vozes terminam. **Risco:** pausa de GC gen2 durante uma troca. Medir com o harness; mitigação possível: `GCSettings.LatencyMode = SustainedLowLatency` enquanto o áudio roda [inferência].

## 6. Sincronização PC ↔ celular e queda do Wi-Fi

- **Fonte única de verdade:** o `KitController` no app. A UI do PC **não** chama o `KitEngine` direto para trocar o kit; ela passa pelo mesmo controlador e recebe o mesmo evento `StateChanged(version, snapshot)` (repassado para a thread da UI).
- **SSE:** na conexão o servidor manda primeiro o `state` completo, com `id: <version>`; depois manda `state` a cada mudança. Na reconexão o navegador reenvia `Last-Event-ID`, e o servidor sempre responde com o retrato completo, sem replay de eventos. É simples e não perde nada [inferência].
- **Medidor e golpes:** `ReadAndResetInputPeak` e `TryDequeueEvent` são de **consumidor único**. O app tem um único *sampler* (30 Hz) que lê o motor e distribui para a UI do PC e para o hub web. O hub web nunca chama o motor.
- **Wi-Fi cai:**
  - O áudio não percebe: o Kestrel roda no thread pool e nunca bloqueia o callback ASIO, e o último kit aplicado continua tocando.
  - O celular mostra a faixa **"Sem conexão com o PC"**, desabilita os botões e reconecta sozinho (EventSource). Ao voltar, recebe o retrato completo.
  - Uma operação que estava em andamento termina no PC de forma atômica (aplicada inteira ou não aplicada). O celular consulta `GET /api/operations/{opId}`.
- **Falhas do servidor web** (porta ocupada, exceção) aparecem no log do PC e desligam só o painel.

## 7. Plano de testes

1. **Automáticos sem rede (CI local):**
   - `KitController` em Core (fila, last-writer-wins, `superseded`, falha sem tocar no kit ativo, versão, persistência) com `PrepareAsync` falso.
   - API com `WebApplicationFactory`/`TestServer` [fonte]: pareamento (código certo, errado, expirado, rate limit), Origin/Host inválidos dão 403, cookie revogado dá 401, `sampleId` com `..` dá 400, idempotência por `clientOpId`, SSE envia o `state` inicial e o próximo `version`.
   - Teste de troca de kit **durante o processamento** em blocos de 32 a 1024: as vozes antigas terminam, a troca acontece no início de um bloco e não há alocação no `Process` (já coberto pelo padrão do Core).
2. **Localhost com o harness:** `SampleDrum.App.exe --simulation` (worker do harness) e `http://localhost:8750` no navegador do PC. Trocar presets durante a bateria simulada e conferir os eventos, a versão e a ausência de *glitch* no medidor de saída.
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
| M1 Core | `KitController` (fila, versão, ops, eventos) + ajustes de API (seção 9) + testes | 1 dia |
| M2 `src/SampleDrum.Remote` (net10.0, FrameworkReference AspNetCore) | Host Kestrel inicia/para pelo app, endpoints, SSE, pareamento, store de tokens (hash no `AppConfig`), Origin/Host, rate limit, arquivos embutidos; `tests/SampleDrum.Remote.Tests` com `Microsoft.AspNetCore.Mvc.Testing` | 2 dias |
| M3 Página do celular | `wwwroot/` (HTML/CSS/JS puro, pt-BR, botões grandes, modo escuro, faixa offline, confirmações) | 1–1,5 dia |
| M4 Integração na UI do PC | Seção "Painel do celular" (liga/desliga, URL + QR, código de pareamento, aparelhos autorizados, "Permitir teste pelo celular", botão de firewall `--setup-firewall` elevado, aviso de rede Pública); UI do PC passando pelo `KitController` | 1 dia |
| M5 Validação | Localhost + harness, celulares reais, documentação `docs/painel-movel.md` (uso) | 0,5–1 dia |

**Total ≈ 5,5 a 6,5 dias de worker.**

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

## 9. O que o painel precisa da API Core (a confirmar quando o contrato for publicado)

- **Versão do estado** (`long Version`) e um evento `KitChanged` no `KitController`. O motor de áudio não precisa disso.
- `PreparedKit` composto: `PreparedKit.With(DrumPiece p, PreparedSample s)`, ou `PrepareAsync` de uma peça só, reaproveitando as outras peças já preparadas (evita recarregar 4 WAVs para trocar 1).
- **Resultado do prepare por peça**, com código de erro estável (`file_not_found`, `unsupported_format`, `too_long`, `outside_library`), além da mensagem em pt-BR.
- `SampleLibrary` com **id relativo** e resolução segura (`TryResolve(string id, out string fullPath)`, que rejeita caminhos fora da raiz).
- `ActiveKit` com o nome e o id do preset aplicado (para o `state`) e o `AppConfig` com a lista de aparelhos pareados (nome, hash do token, datas).
- Manter `ReadAndResetInputPeak` e `TryDequeueEvent` de consumidor único e documentar que o app faz a distribuição.

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
