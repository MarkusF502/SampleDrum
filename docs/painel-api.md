# API do painel do celular (contrato v3)

Contrato entre o servidor embutido no SampleDrum (`src/SampleDrum.Remote`), a página do celular
(`src/SampleDrum.Remote/wwwroot`) e os testes. Mudanças entram **aqui primeiro**; nenhum lado muda o formato em silêncio.
Plano e motivos: `docs/research/painel-movel.md`.

## 1. Visão geral

- Servidor HTTP (Kestrel) **dentro do app**, porta padrão **8750**, desligado por padrão. Escuta só no IP da rede escolhida
  no PC e no loopback (`127.0.0.1`). `0.0.0.0` só se o operador escolher explicitamente "todas as redes".
- HTTP simples (sem HTTPS), só para rede local confiável. Comandos por `POST` com JSON; estado ao vivo por **SSE**
  (`EventSource`).
- JSON em `camelCase`, UTF-8. Erros em `application/problem+json` (seção 7).
- Ids de peça: `kick` (Bumbo), `snare` (Caixa), `tom` (Tom), `floorTom` (Surdo). Diferencia maiúsculas.
- O celular **nunca** envia caminho de arquivo: só `sampleId` (vindo de `GET /api/samples`) e `presetId` (vindo de
  `GET /api/presets`).

## 2. Rotas

| Método e rota | Acesso | Resposta |
|---|---|---|
| `GET /` | público | Página do painel (`wwwroot/index.html`) |
| `GET /assets/{arquivo}` | público | Arquivo estático `wwwroot/{arquivo}` (ex.: `/assets/app.js` → `wwwroot/app.js`) |
| `POST /api/pair` | público | `204` + cookie; `401 pairing_invalid`; `429 pairing_locked`; `409 too_many_devices` |
| `POST /api/logout` | pareado | `204` (esquece este aparelho e apaga o cookie) |
| `GET /api/state` | pareado | `200` estado (seção 4) |
| `GET /api/events` | pareado | `200 text/event-stream` (seção 5) |
| `GET /api/samples` | pareado | `200` lista de samples |
| `GET /api/presets` | pareado | `200` lista de presets |
| `POST /api/pieces/{piece}/sample` | pareado | `202 {opId}`; `409 stale_state`; `400`/`404` |
| `POST /api/kit/preset` | pareado | `202 {opId}`; `409 stale_state`; `400`/`404` |
| `POST /api/pieces/{piece}/test` | pareado | `202 {opId}`; `403 test_not_allowed`; `409 audio_stopped` / `no_sample` |
| `GET /api/operations/{opId}` | pareado | `200` operação; `404 operation_not_found` |

Qualquer outra rota: `404 not_found`. `OPTIONS` (preflight CORS) e métodos não listados: `405 method_not_allowed`,
**sem** cabeçalhos `Access-Control-*` (CORS negado).

### Arquivos estáticos

- Tudo de `wwwroot/` vai embutido no executável e é servido da memória. `index.html` é servido em `/`; qualquer outro
  arquivo em `/assets/<caminho relativo a wwwroot>`. Subpastas valem (`wwwroot/icons/a.png` → `/assets/icons/a.png`).
- Tipos: `.html`, `.css`, `.js`, `.json`, `.webmanifest`, `.svg`, `.png`, `.ico`, `.woff2`. Outros: `application/octet-stream`.
- Cabeçalhos em todas as respostas: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `X-Frame-Options: DENY`, `Cache-Control: no-store` (API) / `no-cache` + `ETag` (estáticos) e
  `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`.
  **Consequência para a página:** nada de `<script>` ou `<style>` inline, nem atributos `style="..."`/`onclick="..."`
  no HTML; use `app.js`/`app.css` (alterar `elemento.style` pelo JS é permitido).

## 3. Pareamento e acesso

### Código e QR

- O PC mostra um **código de 6 dígitos** válido por **5 minutos**, a URL e um QR code com
  `http://<IP>:<porta>/#pair=<código>`. A página lê `#pair=`, preenche o código e apaga o fragmento
  (`history.replaceState`). O fragmento nunca vai ao servidor.
- O código é de **uso único**: depois de um pareamento certo, o PC mostra outro. Ele também é renovado quando vence,
  pelo botão "Gerar novo código" e depois de **10 erros** com o mesmo código.

### `POST /api/pair`

```http
POST /api/pair HTTP/1.1
Host: 192.168.0.10:8750
Origin: http://192.168.0.10:8750
Content-Type: application/json

{"code":"123456","deviceName":"Celular do Lucas"}
```

- `204 No Content` + `Set-Cookie: sd_device=<token>; Max-Age=34560000; Path=/; HttpOnly; SameSite=Strict`.
- `deviceName` opcional (até 40 caracteres; vazio vira "Celular"). Aparece na lista do PC.
- `401 pairing_invalid`: código errado, vencido, ausente ou fora do formato (conta como tentativa errada).
- `429 pairing_locked` + `Retry-After: <segundos>`: 5 erros em 1 minuto vindos do mesmo IP bloqueiam esse IP até a
  janela passar; 20 erros em 1 minuto (todos os IPs) bloqueiam o pareamento para todos.
- `409 too_many_devices`: já há 16 aparelhos pareados (remova algum no PC).

### Token e cookie

- Token aleatório de **256 bits** (base64url, 43 caracteres), **só** no cookie `sd_device`
  (`HttpOnly; SameSite=Strict; Path=/`, sem `Secure` porque é HTTP). Nunca aparece em URL, query string, corpo de
  resposta, log ou evento SSE. O PC guarda só o **SHA-256** (`AppConfig.RemotePanel.PairedDevices`), com nome, data do
  pareamento e último acesso.
- Sem cookie, cookie inválido ou aparelho removido: `401 not_paired` em todas as rotas pareadas. A página então volta à
  tela de pareamento.
- **Revogação** ("Remover"/"Remover todos" no PC): efeito imediato; as conexões SSE do aparelho recebem `revoked` e
  são fechadas; as próximas requisições dão `401 not_paired`.
- `POST /api/logout` (corpo `{}`): remove este aparelho, fecha as conexões SSE dele e apaga o cookie (`Max-Age=0`).

### Regras para toda requisição

1. **Host** permitido (senão `403 host_not_allowed`): `<IP escolhido>:<porta>`, `localhost:<porta>`,
   `127.0.0.1:<porta>`, `[::1]:<porta>` e `<NOMEDOPC>.local:<porta>` (sem diferenciar maiúsculas). Com "todas as
   redes", vale também qualquer IPv4 deste PC. Bloqueia *DNS rebinding*.
2. **Rede**: cliente fora da sub-rede da interface escolhida (e fora do loopback) → `403 network_not_allowed`.
3. **POST** (todo POST, inclusive `/api/pair` e `/api/logout`):
   - `Origin` obrigatório e igual a `http://` + um Host permitido → senão `403 origin_not_allowed`.
   - `Content-Type: application/json` (com ou sem `; charset=utf-8`) → senão `415 unsupported_media_type`.
   - Corpo JSON com um **objeto** (`{}` quando não há campos) → senão `400 invalid_json`. Máximo 8 KB → `413 payload_too_large`.
4. **Limites**: comandos **aceitos** (`sample`, `preset`, `test`) até 5 por segundo por aparelho; o 6º →
   `429 rate_limited` + `Retry-After` (pedidos recusados por validação ou `stale_state` não contam); no máximo **4 conexões SSE por aparelho** (`429 too_many_connections`) e 32 no total.

## 4. Estado

`GET /api/state` e o evento SSE `state` trazem o mesmo objeto:

```json
{
  "version": 42,
  "kitVersion": 40,
  "profile": "Igreja",
  "simulation": false,
  "audioRunning": true,
  "kitEnabled": true,
  "kit": { "name": "Rock 1", "presetId": "rock-1" },
  "pieces": [
    { "piece": "kick", "label": "Bumbo", "enabled": true, "route": "ok",
      "sample": { "id": "bumbos/kick-seco.wav", "name": "kick-seco", "durationMs": 420 },
      "outputGainDb": 0, "sampleVersion": 40 },
    { "piece": "snare", "label": "Caixa", "enabled": true, "route": "pending",
      "sample": null, "outputGainDb": -3, "sampleVersion": 12, "pendingOpId": "4f1c2b0e-..." }
  ],
  "allowPhoneTest": false,
  "device": { "name": "Celular do Lucas" }
}
```

- `version`: versão do estado no PC (sobe 1 a cada mudança aplicada, vinda do PC ou de qualquer celular). Volta a 0
  quando o app reinicia.
- `kitVersion`: versão da última mudança de **seleção** do kit (sample de qualquer peça ou preset ativo).
- `pieces[].sampleVersion`: versão da última troca de sample **desta** peça.
- `kit.presetId`: preset aplicado inteiro; `null` depois de trocar uma peça sozinha (seleção personalizada).
- `pieces[].sample`: `null` = sem sample. `id` é `null` quando o arquivo está fora da biblioteca (escolhido no PC);
  `durationMs` pode ser `null` por um instante logo depois de uma troca.
- `route`: `"ok"` = a peça tem entrada e saída no perfil ativo; `"pending"` = sem rota (nada sai na mesa).
- `pendingOpId`: presente quando há uma operação **do painel** (de qualquer celular) ainda `queued`/`preparing` para
  essa peça (um preset pendente aparece em todas).
- `audioRunning`: o áudio (ASIO ou simulação) está rodando. `simulation`: app aberto com `--simulation` (kit e
  biblioteca de demonstração; nada é salvo na configuração da igreja).
- `allowPhoneTest`: o PC permitiu o botão "Testar" no celular.
- `device.name`: o nome deste aparelho.

### `GET /api/samples`

```json
[ { "id": "bumbos/kick-seco.wav", "name": "kick-seco", "durationMs": 420, "sampleRate": 48000, "channels": 1 } ]
```

`id` = caminho relativo à pasta de samples, com `/`. Ordenado por nome.

### `GET /api/presets`

```json
[ { "id": "rock-1", "name": "Rock 1", "available": true,
    "pieces": {
      "kick":     { "sampleId": "bumbos/kick-seco.wav", "sampleName": "kick-seco", "gainDb": 0 },
      "snare":    { "sampleId": "caixas/caixa-1.wav", "sampleName": "caixa-1" },
      "tom":      { "sampleId": null, "sampleName": null },
      "floorTom": { "sampleId": null, "sampleName": "surdo-externo" } } } ]
```

- `id` = nome do arquivo do preset sem `.json`. As quatro chaves sempre existem.
- `sampleId: null` + `sampleName: null` = a peça fica sem sample; `sampleId: null` + `sampleName` = arquivo fora da
  biblioteca (o preset ainda pode ser aplicado). `gainDb` só aparece quando o preset define ganho.
- `available: false` = algum arquivo do preset não existe; aplicar vai falhar com erros por peça e o kit não muda.

## 5. Eventos (SSE) — `GET /api/events`

`Content-Type: text/event-stream`; a primeira linha é `retry: 3000`. Mensagens:

| Evento | `id` | `data` | Quando |
|---|---|---|---|
| `state` | `version` | estado completo (seção 4) | logo ao conectar e a cada mudança (PC ou celular, áudio, rotas, permissão de teste) |
| `op` | — | operação (seção 6) | mudança de status (ou confirmação do motor) das operações **deste** aparelho |
| `meters` | — | `{"kick":-18.5,"snare":-90,"tom":-90,"floorTom":-42.1}` | ~5 vezes por segundo; dBFS do pico de entrada, piso -90 |
| `hit` | — | `{"piece":"kick","velocity":0.71,"test":false}` | cada disparo (ou teste) |
| `revoked` | — | `{"reason":"revoked"}` | o aparelho foi removido/deslogado; o servidor fecha a conexão em seguida |
| comentário `: ping` | — | — | a cada ~15 s (mantém a conexão) |

- **Reconexão:** o `EventSource` reconecta sozinho; o servidor **ignora** `Last-Event-ID` e manda de novo o `state`
  completo (sem replay de eventos antigos). O celular **nunca reenvia** comandos antigos ao reconectar; para saber o
  que aconteceu com um comando em andamento, consulta `GET /api/operations/{opId}`.
- Se o `EventSource` der erro, a página chama `GET /api/state`: `401` = parear de novo; falha de rede = faixa
  "Sem conexão com o PC" e botões desabilitados até voltar.
- Painel desligado no PC ou Wi-Fi caído: só o controle remoto para; o áudio e o kit ativo continuam no PC.

## 6. Comandos, ordem e status

### Trocar o sample de uma peça

```http
POST /api/pieces/snare/sample
Content-Type: application/json
Origin: http://192.168.0.10:8750

{"sampleId":"caixas/caixa-2.wav","clientOpId":"3b2f8a5c-6f1e-4c7a-9d51-2a7f0c9b1e44","baseVersion":42}
```

→ `202 {"opId":"9a0e..."}`

### Aplicar um preset (kit inteiro)

```http
POST /api/kit/preset
{"presetId":"rock-1","clientOpId":"...","baseVersion":42}
```

→ `202 {"opId":"..."}`. Todas as peças são preparadas antes; se **qualquer** uma falhar, nenhuma muda.

### Testar uma peça

```http
POST /api/pieces/kick/test
{"clientOpId":"..."}
```

- `403 test_not_allowed` se "Permitir teste pelo celular" estiver desligado no PC (padrão). `409 audio_stopped` com o
  áudio parado; `409 no_sample` se a peça não tem sample. Senão `202 {opId}` (operação `kind: "test"`, já `applied`).
- O teste toca o sample **na mesa** (pode sair no PA/transmissão): a página pede confirmação antes de enviar.

### Ordem: `baseVersion`, `clientOpId` e `409 stale_state`

- `clientOpId`: UUID gerado pelo celular para cada **intenção** do operador. Repetir o mesmo `clientOpId` (ex.: rede
  caiu e o POST foi reenviado) devolve **a mesma operação** (`202` com o mesmo `opId`), sem enfileirar outra.
  O mesmo `clientOpId` com outro conteúdo → `409 client_op_reused`. Vale por aparelho, para as últimas 512 operações.
- `baseVersion`: o `version` do estado que o celular estava mostrando quando o operador tocou. Obrigatório nos
  comandos `sample` e `preset` (`400 missing_base_version`).
- **Recusa por estado velho** (`409 stale_state`, corpo com `state` atual):
  - troca de peça: se o sample **dessa peça** mudou depois de `baseVersion` (`pieces[].sampleVersion > baseVersion`);
  - preset: se **qualquer** peça ou o preset ativo mudou depois de `baseVersion` (`kitVersion > baseVersion`);
  - `baseVersion` maior que o `version` atual (o app reiniciou): também `409 stale_state`.
  - Mudanças de ganho/ajustes/ativação feitas no PC **não** tornam o pedido velho.
- Com `409 stale_state` a página mostra "alterado em outro dispositivo", atualiza a tela com o `state` recebido e só
  reenvia se o operador tocar de novo (com **novo** `clientOpId` e o novo `baseVersion`). Nunca reenvia sozinha.
- Pedidos aceitos entram na fila única do PC (`KitController`). Um pedido mais novo para a mesma peça (ou um preset)
  pode substituir um mais antigo ainda pendente: o antigo termina como `superseded`.

### `GET /api/operations/{opId}` e evento `op`

```json
{ "opId": "9a0e...", "clientOpId": "3b2f...", "kind": "pieceSample", "piece": "snare",
  "sampleId": "caixas/caixa-2.wav", "status": "applied", "engineConfirmed": true, "audioRunning": true,
  "errors": [], "resultVersion": 43 }
```

- `kind`: `pieceSample` | `preset` | `test` (pedidos do celular). Operações feitas no PC, consultadas pelo `opId`, podem
  ter também `kit`, `pieceGain`, `pieceEnabled`, `pieceSettings`, `kitEnabled`, `libraryFolder`. `piece` só quando a
  operação é de uma peça; `presetId` só em `preset`; `sampleId` só em `pieceSample` do celular.
- `status`: `queued` → `preparing` → `applied` | `failed` | `superseded`.
- `engineConfirmed`: `true` quando a thread de áudio **já tocou com o kit** desta operação
  (`KitEngine.AppliedKitGeneration` alcançou a geração produzida por ela) com o áudio rodando. Com o áudio parado fica
  `false` com `status: "applied"` → a página mostra "aplicado; áudio parado". Uma operação mais nova que também já foi
  pega pelo áudio confirma as anteriores.
- **A página só muda a seleção mostrada** depois de `applied` + `engineConfirmed: true`, ou `applied` com
  `audioRunning: false` — nunca de forma otimista. Em `failed`, mostra os `errors` e mantém a seleção anterior; em
  `superseded`, informa que um pedido mais novo venceu.
- `errors[]`: `{ "piece": "snare" | null, "code": "...", "message": "texto pt-BR" }`. Códigos: `file_not_found`,
  `unsupported_format`, `too_long`, `outside_library`, `io_error`, `invalid_argument`, `internal`.
- `resultVersion`: `version` do estado que essa operação produziu (só em `applied`).
- Operações do PC também podem ser consultadas pelo `opId` (com `clientOpId: null`); o histórico guarda as últimas 256.
- O evento `op` usa o mesmo objeto e só vai para as conexões do aparelho que fez o pedido; ele é enviado em cada
  mudança de status e quando `engineConfirmed` passa a `true`.

## 7. Erros

```json
{ "type": "urn:sampledrum:stale_state", "title": "Estado alterado em outro dispositivo", "status": 409,
  "detail": "A Caixa mudou depois da versão 42. Confira e toque de novo.", "code": "stale_state", "state": { ... } }
```

| HTTP | `code` | Quando |
|---|---|---|
| 400 | `invalid_json` | corpo não é um objeto JSON válido |
| 400 | `invalid_client_op_id` | `clientOpId` ausente ou não é UUID |
| 400 | `missing_base_version` | `baseVersion` ausente ou negativo |
| 400 | `invalid_sample_id` | `sampleId` ausente ou inválido (`..`, caminho absoluto, etc.) |
| 400 | `invalid_preset_id` | `presetId` ausente |
| 401 | `not_paired` | sem cookie, cookie inválido ou aparelho removido |
| 401 | `pairing_invalid` | código de pareamento errado/vencido |
| 403 | `host_not_allowed` | cabeçalho `Host` fora da lista |
| 403 | `origin_not_allowed` | POST sem `Origin` ou com `Origin` fora da lista |
| 403 | `network_not_allowed` | cliente fora da rede local escolhida |
| 403 | `test_not_allowed` | teste pelo celular desligado no PC |
| 404 | `not_found` | rota inexistente |
| 404 | `unknown_piece` | peça diferente de `kick`/`snare`/`tom`/`floorTom` |
| 404 | `sample_not_found` | `sampleId` não existe na biblioteca |
| 404 | `preset_not_found` | `presetId` não existe na pasta de presets |
| 404 | `operation_not_found` | `opId` desconhecido ou já fora do histórico |
| 405 | `method_not_allowed` | método não suportado (inclui `OPTIONS`) |
| 409 | `stale_state` | estado mudou depois de `baseVersion` (traz `state`) |
| 409 | `client_op_reused` | mesmo `clientOpId` com outro pedido |
| 409 | `audio_stopped` | teste com o áudio parado |
| 409 | `no_sample` | teste de peça sem sample |
| 409 | `too_many_devices` | 16 aparelhos já pareados |
| 413 | `payload_too_large` | corpo acima de 8 KB |
| 415 | `unsupported_media_type` | POST sem `Content-Type: application/json` |
| 429 | `pairing_locked` | muitas tentativas de pareamento (`Retry-After`, `retryAfterSeconds`) |
| 429 | `rate_limited` | mais de 5 comandos por segundo (`Retry-After`) |
| 429 | `too_many_connections` | mais de 4 SSE por aparelho (ou 32 no total) |
| 500 | `internal` | erro inesperado (o áudio continua) |

## 8. O que o celular pode e não pode

- **Pode:** ver o estado, trocar o sample de uma peça (só da biblioteca), aplicar um preset inteiro, testar uma peça
  (só se permitido no PC, com confirmação), sair (logout).
- **Não pode:** enviar arquivos, navegar em pastas do PC, mudar driver/ASIO, rotas, canais USB, nível da mesa, ganho,
  detecção, ativar/desativar o kit, parear outros aparelhos ou mudar a configuração do painel.

## 9. Exemplos com `curl` (no próprio PC)

```powershell
# o código aparece no PC; guarda o cookie em cookies.txt
curl.exe -i -c cookies.txt -H "Origin: http://127.0.0.1:8750" -H "Content-Type: application/json" `
  -d '{\"code\":\"123456\",\"deviceName\":\"teste\"}' http://127.0.0.1:8750/api/pair
curl.exe -b cookies.txt http://127.0.0.1:8750/api/state
curl.exe -N -b cookies.txt http://127.0.0.1:8750/api/events
curl.exe -b cookies.txt -H "Origin: http://127.0.0.1:8750" -H "Content-Type: application/json" `
  -d '{\"presetId\":\"rock-1\",\"clientOpId\":\"3b2f8a5c-6f1e-4c7a-9d51-2a7f0c9b1e44\",\"baseVersion\":0}' `
  http://127.0.0.1:8750/api/kit/preset
```
