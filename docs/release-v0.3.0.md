# SampleDrum v0.3.0 — PC e painel mobile

Pré-lançamento validado em Windows x64, em 27/11/2026. O painel mobile roda no navegador Android/iPhone e já está embutido no aplicativo Windows. O ZIP mobile é uma cópia dos arquivos web para consulta e desenvolvimento; depende da API do PC.

## Conclusão da revisão

- Pareamento de 8 dígitos, janela de 5 minutos, uso único. Uso, vencimento ou 10 erros fecham a janela; o operador precisa gerar outro código no PC. Isso elimina a exposição contínua a tentativas que existia com renovação automática. Um cliente na rede ainda pode atrapalhar uma janela aberta com tentativas erradas; ele não consegue reabri-la remotamente. Celulares autorizados continuam conectados.
- A seleção por celular confere versão e operações pendentes dentro do mesmo lock do controlador. Trocas sobrepostas ainda em preparação no PC dão `409 stale_state`, preservando a intenção do operador. A thread de áudio não usa esse lock.
- Mantidos os controles revisados: Host/Origin, JSON obrigatório, sem CORS, token aleatório com apenas hash persistido, revogação de SSE, IDs limitados à biblioteca, erros sem caminhos do PC e confirmação do motor antes de mostrar a troca aplicada.
- Simulação não cria nem altera a configuração da igreja.

## Verificações realizadas

- `dotnet test SampleDrum.sln -c Release`: **435 aprovados** (Core 330, Remote 93, App 12).
- Testes Remote incluem Kestrel real em loopback, áudio simulado, trocas com retorno gravado, preset quebrado sem alteração, reconexão sem replay, token inválido, revogação, conflito, idempotência, limites de SSE e lifecycle.
- Publicação self-contained `win-x64`, sem instalar .NET: compilação sem avisos ou erros.
- Executável publicado: `--selftest` **13/13**, `--harness` e `--harness-swap`, todos com código de saída 0.
- Navegador contra o aplicativo publicado com `--simulation`, em loopback: código de 8 dígitos/QR, pareamento, preset Demo Suave aplicado com áudio rodando, bumbo trocado para bumbo-seco, medidores ao vivo e confirmação de aplicação. PC mostrou pareamento fechado após o uso. Parar o painel manteve o áudio em execução.

## Pacotes e reprodução

Execute `./scripts/Publish-Release.ps1` na raiz para gerar os dois ZIPs e `SHA256SUMS.txt` em `artifacts/release` (a pasta de saída deve estar vazia). O script apenas compila e empacota; publicar no GitHub é uma ação separada.

- Windows: extraia o ZIP completo e execute `SampleDrum.App.exe`. `Iniciar-simulacao.cmd` abre a demonstração sem ASIO.
- Celular: no app Windows, ative **Painel do celular**, escolha a rede local e leia o QR. Gere outro código no PC para adicionar outro aparelho.

## Validação que depende do ambiente da igreja

Ainda requer ensaio com X32/X-USB e OBS: compartilhamento do driver ASIO, rotas das peças pendentes, latência física e calibração com gravações reais. Acesso por um celular físico via Wi-Fi também depende dessa rede/firewall. Por isso a versão continua marcada como pré-lançamento. O painel usa HTTP em rede local confiável, conforme o README.
