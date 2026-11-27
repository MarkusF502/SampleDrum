# SampleDrum Mobile — Android e iPhone

Companheiros nativos do painel do PC: Android em Java/AndroidX e iOS em SwiftUI/WKWebView. Eles oferecem conexão por QR ou endereço e guardam o último PC. A tela de controle é servida pelo aplicativo Windows, mantendo uma única interface e as mesmas regras de pareamento, conflitos e confirmação pelo áudio. O celular não processa nem transmite o áudio.

Compatível com o PC **v0.3.0 ou posterior**, que usa código de 8 dígitos. Android 8+ e iOS 16+.

## Usar

1. Abra o SampleDrum no PC. Ative **Painel do celular** na interface de rede local, em vez de localhost.
2. Conecte o celular à mesma rede. Abra o app e toque em **Ler QR do PC**; permita a câmera quando solicitado. Também é possível digitar o endereço mostrado no PC.
3. O QR preenche o código; toque em **Parear**. Pela entrada manual, digite o código de 8 dígitos mostrado no PC. Ele vale 5 minutos e serve uma vez. Gere outro no PC para adicionar outro aparelho.
4. Troque samples ou presets. **Trocar PC** volta à conexão; o áudio continua no computador.

Se o PC mudar de IP, informe o endereço novo. Verifique a rede e o firewall quando o PC não for alcançável. Fechar o app não revoga o pareamento; use **Desparear** no painel ou remova o celular no PC. O navegador e o aplicativo usam cookies separados: pareie o aplicativo mesmo se o navegador já estiver autorizado.

## Android: APK

Instale o APK assinado disponibilizado na release mobile. O Android pode pedir autorização para instalar pelo navegador usado no download. A câmera só é usada pelo leitor de QR nativo; nenhum áudio é capturado.

Para compilar: JDK 17, Android SDK com plataforma 36 e Build Tools 35.0.0. Configure `JAVA_HOME` e `ANDROID_HOME` ou `local.properties` (arquivo local ignorado).

```powershell
cd mobile/android
./gradlew.bat testDebugUnitTest lintDebug assembleRelease
# Com um emulador ou dispositivo autorizado conectado:
./gradlew.bat connectedDebugAndroidTest
```

O resultado de release é `app/build/outputs/apk/release/app-release-unsigned.apk`. Assine com `scripts/Sign-Android.ps1`, informando `-InputApk`, `-OutputApk`, `-BuildToolsDirectory` e `-JavaDirectory`.

A primeira assinatura gera uma chave PKCS#12 e uma senha aleatória em `mobile/android/.signing/`, fora do Git. **Preserve essa pasta de forma privada**: atualizações do mesmo aplicativo precisam da mesma chave. O script verifica assinatura v2/v3 e alinhamento. Não publique a chave nem a senha; o workflow produz somente APK sem assinatura e nunca recebe esses segredos.

## iPhone: projeto e distribuição

O projeto é gerado com XcodeGen no Mac:

```sh
brew install xcodegen
cd mobile/ios
swift test --package-path Policy
xcodegen generate
open SampleDrumMobile.xcodeproj
```

Selecione sua equipe em **Signing & Capabilities**, confirme o identificador `com.markusf502.sampledrum` na sua conta e arquive com Xcode para distribuir por TestFlight/App Store. Essa distribuição requer conta Apple Developer, certificado e provisionamento autorizados pelo proprietário da conta; o repositório não contém credenciais ou assinatura Apple. O ZIP de simulador e a compilação sem assinatura para dispositivo são artefatos de desenvolvimento, **não instaladores de iPhone**.

O app explica os acessos à câmera e à rede local no pedido de permissão do iOS. O manifesto de privacidade declara preferências somente do próprio aplicativo; não há analytics nem publicidade.

## Limites da conexão

- Só aceita IPv4 privado (`10/8`, `172.16/12`, `192.168/16`) ou nome `PC.local`, porta 1024–65535 e URL raiz do painel. Links públicos, credenciais na URL, caminhos extras, query strings e códigos fora do formato são recusados.
- Somente recursos do PC selecionado são carregados no painel. Não há ponte JavaScript nativa, upload de arquivos ou câmera disponível para a página do PC.
- Código e token não são guardados nas preferências. O cookie HttpOnly é mantido pelo WebView/WKWebView para o pareamento continuar entre aberturas. No Android, backup do app é desativado.
- O protocolo do PC continua sendo HTTP em rede local confiável; instalar o aplicativo não transforma a conexão em HTTPS. Revogue os celulares no PC se necessário.
- Não há descoberta que varra a rede nem reconexão automática ao abrir o app. O último endereço fica preenchido para o operador confirmar.
- Target Android 36: acesso LAN usa a permissão `INTERNET`; ao migrar para target 37, implementar a permissão de rede local do Android 17.

## Verificação automatizada

O workflow `Mobile Android and iOS` compila os dois aplicativos e exercita entradas válidas/inválidas, isolamento de origem e separação entre URL salva e código. Android executa lint e teste de interface em emulador; iOS compila para simulador e dispositivo e executa teste de interface no simulador. Os relatórios e capturas ficam nos artefatos do workflow. Leitura real de QR, permissões de câmera/rede e comportamento no Wi-Fi da igreja precisam de verificação em celulares físicos.
