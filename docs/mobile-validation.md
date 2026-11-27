# Validação do SampleDrum Mobile v0.4.0

27/11/2026. Companheiros nativos para controlar o painel do PC v0.3.0.

- Android: compilação release, lint e testes unitários de endereço/origem aprovados. O teste de interface no emulador Android 15 abre a tela de conexão e verifica a recusa de um endereço externo. Captura da tela inicial conferida.
- iOS: testes de endereço/origem aprovados; compilação para simulador e para iPhone sem assinatura. O teste de interface abre a tela e verifica a recusa de endereço externo.
- Execução automatizada: [workflow mobile](https://github.com/MarkusF502/SampleDrum/actions/runs/46042441856). O código das telas e regras testadas é o mesmo do aplicativo distribuído; a etapa final de empacotamento inclui os avisos Apache 2.0 das dependências no APK.
- APK de release alinhado e assinado com RSA de 3072 bits; `apksigner verify` confirma os esquemas v2 e v3. `zipalign -c -P 16 4` aprovado. Chave privada e senha ficam fora do Git, dos pacotes e do CI, com cópia privada no diretório comum Git do repositório.
- O código e o token de pareamento não entram nas preferências nativas. A URL salva contém apenas o endereço do PC; o cookie HttpOnly permanece no armazenamento do WebView.
- A página remota não tem ponte para APIs nativas. Os carregamentos ficam restritos à origem escolhida. Câmera nativa é usada somente para leitura de QR.

Os testes não substituem a conferência de câmera, permissões e conectividade em Android/iPhone físicos na rede da igreja. Não foi publicada uma distribuição iOS instalável: a conta Apple Developer e a assinatura para TestFlight/App Store ainda não estão disponíveis. O ZIP iOS contém o projeto para desenvolvimento.

O protocolo continua sendo HTTP na rede local confiável, e o áudio continua no PC. Não houve alteração no servidor ou no motor de áudio nesta entrega.
