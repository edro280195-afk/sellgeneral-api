# Autenticación telefónica de Nenis con Firebase

## Arquitectura

La aplicación móvil usa `firebase_auth` para enviar y validar el OTP por SMS
con `verifyPhoneNumber`. Después obtiene un Firebase ID token y lo envía a
`POST /api/auth/firebase`. La API valida firma, emisor, audiencia, expiración y
estructura mediante Firebase Admin SDK; obtiene el teléfono del usuario
validado; vincula o crea la cuenta local; y finalmente emite el JWT y refresh
token propios de Nenis.

Firebase no es la base de datos de Nenis ni decide tenants, memberships, roles,
permisos, suscripciones o acceso a recursos.

## Desarrollo local

1. Usa el proyecto Firebase de Nenis (`nenisapp-60810`) en la configuración de
   la aplicación móvil.
2. En Firebase Console habilita Authentication > Sign-in method > Phone.
3. Para pruebas automatizadas no se envían SMS: las pruebas de Flutter usan
   la utilidad de normalización y el mapeo de errores, y las pruebas de API
   mockean `IFirebaseAuthService`.
4. Para pruebas manuales agrega números de prueba en Authentication >
   Sign-in method > Phone > Phone numbers for testing. Usa únicamente esos
   números y códigos controlados por Firebase; no hardcodees teléfonos reales.
5. El backend debe recibir una credencial administrativa del mismo proyecto
   Firebase. Configura `Firebase__ServiceAccountPath` con la ruta absoluta al
   JSON fuera del repositorio, o `GOOGLE_APPLICATION_CREDENTIALS` para usar
   Application Default Credentials.

El archivo `firebase-service-account.json` está ignorado por Git. No copies
claves privadas a `appsettings.json`, al APK, al bundle iOS ni a esta
documentación.

### Segundo proyecto de Firebase para choferes (opcional)

Un token FCM solo lo acepta el proyecto con el que se registró. Los avisos a
choferes salen por `IDriverFcmService`, que usa una segunda `FirebaseApp`
llamada `drivers` si existe una credencial aparte: `Firebase:DriversServiceAccountPath`
(por defecto `firebase-drivers-service-account.json`, también se busca en
`/etc/secrets/`, donde Render monta los Secret Files). Sin ese archivo, los
choferes usan la misma credencial que la app. El archivo está ignorado por Git.

## Android

La aplicación ya tiene:

- `applicationId`: `com.nenisapp.nenis_app`.
- `namespace`: `com.nenisapp.nenis_app`.
- `android/app/google-services.json` para el proyecto configurado en ese
  archivo.
- El plugin `com.google.gms.google-services` aplicado en Gradle.

En Firebase Console agrega o confirma la aplicación Android con ese package
name. Registra el SHA-1 y SHA-256 de cada certificado que se use: debug local,
CI y release/Play App Signing. Después descarga el `google-services.json` nuevo
desde ese mismo proyecto y reemplaza el archivo local si Firebase lo indica.
Para Android publicado, habilita y verifica las protecciones que Firebase
solicite (Play Integrity/SafetyNet según la consola y la versión del proyecto).

## iOS

No existe actualmente `ios/Runner/GoogleService-Info.plist` en el repositorio.
Si se publica iOS:

1. Registra en Firebase la aplicación iOS con el Bundle ID que resuelva
   `$(PRODUCT_BUNDLE_IDENTIFIER)` en Xcode.
2. Descarga `GoogleService-Info.plist` desde el proyecto `nenisapp-60810` y
   agrégalo al target Runner con Copy Bundle Resources.
3. Configura APNs para Firebase si se requiere la parte de mensajería push.
4. Verifica las capacidades y configuración que Phone Auth solicite para el
   entorno iOS (incluido reCAPTCHA/URL scheme si la consola o el SDK lo
   requieren).
5. Prueba en un dispositivo físico; el simulador no representa el flujo real
   de recepción de SMS.

## Migración de cuentas

La API busca primero por `FirebaseUid`. Si no existe, busca por el teléfono
validado en E.164 y también por el formato mexicano legacy de diez dígitos.
Cuando encuentra una cuenta histórica, enlaza el UID, normaliza el teléfono a
E.164 y conserva memberships, negocio, roles y sesión de Nenis. Si no existe,
requiere la aceptación legal y ejecuta el onboarding actual; para seller exige
los datos de negocio existentes.

Las cuentas históricas que no tienen teléfono no se enlazan automáticamente: no
hay una prueba segura de que el número nuevo pertenezca a la misma persona. Si
además se envía un correo ya ocupado, la API rechaza la colisión. Esas cuentas
requieren una recuperación o vinculación explícita desde un flujo administrativo
futuro; no se inventan teléfonos ni se crea una asociación silenciosa.

## Compatibilidad legacy

El flujo principal de la aplicación móvil es Firebase Phone Auth con OTP por
SMS. Los endpoints legacy de contraseña y verificación telefónica se mantienen
solo para compatibilidad con clientes web existentes; el flujo nuevo no depende
de ellos.

## Migración de base de datos

Las migraciones de identidad agregan `FirebaseUid`, un índice único filtrado
para valores no nulos, actualizan el constraint de identidad y eliminan la
integración de identidad social retirada. Revisa y aplica las migraciones con
el procedimiento habitual del entorno; este cambio no ejecuta migraciones
automáticamente contra producción.

## Prueba manual completa

1. Configura Phone Auth y un número de prueba en Firebase.
2. Ejecuta `flutter pub get` y `flutter run` en un dispositivo Android con la
   configuración nativa correcta.
3. Entra a “Entrar con código”, captura el teléfono, confirma el SMS y espera
   el canje contra `/api/auth/firebase`.
4. Comprueba que la respuesta crea una sesión Nenis y que el siguiente
   arranque usa el JWT/refresh token local sin solicitar otro OTP.
5. Cierra sesión y confirma que se limpian la sesión local, el refresh token y
   la sesión de Firebase.
