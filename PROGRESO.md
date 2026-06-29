# Progreso de implementacion - Plataforma SaaS **Neni's App** (Regi Bazar -> multi-tenant)

Tracker de avance sobre el plan `ROLYCONTEXTO.md`.

> **Nombre de plataforma:** Neni's App. Aplica en login, marketing, PWA shell, titulo de ventana y manifest. La marca de CADA tenant (Regi Bazar, Tienda Demo, etc.) sigue siendo per-tenant via FE-0.

## Estado Actual

**Fase 0 - Cimientos backend - 6/6 hecho y validado end-to-end.**
**Fase 1.0 - Motor de entitlements - completo y validado.**
**Fase 1.1 - Gates a features reales - completo y validado.**
**Fase 1.2 - Ciclo de vida de prueba + alta + bloqueo - completo y validado.**
**Fase 1.3 - Suscripcion con Mercado Pago (`preapproval`) - completo y validado.**
**Fase FE-0 - Kit de marca por tenant (backend) - completo y validado.**
**Fase FE-1 - Registro / Acceso sobre Account (Angular) - completo y validado.**
**Fase FE-2 - Alta de negocio + wizard de marca (Angular) - completo y validado.**
**Fase FE-3 - Planes + estado + muro de bloqueo (Angular) - completo y validado.**
**Fase FE-4 - Pago con brick de Mercado Pago (Angular) - completo y validado.**
**Fase M.1 - Migrador consola (one-shot) - completo y validado en build.**
**Fase M.1 - Runbook operativo simplificado - completo.**

**Siguiente:** ejecucion operativa (no hay mas codigo en el plan). Pendientes del checklist: (1) crear branches de Neon "staging-cutover" y "prod-cutover", (2) correr el ensayo en staging hasta dar PASS, (3) promover a "prod-cutover", (4) apuntar la app. Sin "ventana sin live", sin "switch de DNS", sin "rollback 5 min" — el cambio de connection string es un deploy normal.

## Fase 0 - Cimientos Backend

| # | Plan | Estado | Notas |
|---|---|---|---|
| 0.0 | Sincronizar `CLAUDE.md` | Hecho | Realidad del repo documentada. |
| 0.1 | Identidad unificada + tenancy | Hecho | `Account`, `Business`, `Membership`, `BusinessId`, `ICurrentTenant`, seeder DEV y migracion `IdentidadYTenancy`. |
| 0.2 | Auth sobre `Account` + autorizacion server-side | Hecho | Email/password, telefono stub, Facebook stub, JWT `sub=AccountId`, memberships por `X-Business-Id`, migracion `AuthSobreAccountYTenant`. |
| 0.3 | Reclamar perfil | Hecho | `ClientClaimAudit`, `IClientClaimService`, `ClientClaimController`, migracion `ReclamarPerfil` y pruebas. |
| 0.4 | Aislar SignalR por tenant | Hecho | `SignalRGroupNames`, `TenantAwareHubBase` y 5 hubs refactorizados por `BusinessId`. |
| 0.5 | De-hardcodear identidad + storage | Hecho | `ICurrentBusiness`, CORS por tenant, prompts/depot/geocoding/frontend URL por `Business`, Cloudinary por `{slug}`, `/uploads` eliminado. |

## Validacion 0.5

- E2E contra base DEV Neon `sellgeneral`: 61/61 PASS.
- Cubrio auth, guards de tenancy, CRUD de pedidos, rutas + conductor por token, clientes, lealtad, proveedores/inversiones, periodos, finanzas, segundo tenant y aislamiento multi-tenant.
- Bug real corregido: tenants sin `AppSettings` fallaban al crear pedidos. Se agrego `AppDbContext.GetOrCreateTenantSettingsAsync()` con lazy-init por tenant.
- No cubierto sin llaves reales: Cloudinary real, geocoding Google, Gemini/Cami y cobro Mercado Pago.

## Fase 1 - Monetizacion Backend

| # | Plan | Estado | Notas |
|---|---|---|---|
| 1.0 | Motor de entitlements | Hecho | `Feature`, `LimitKey`, `SubscriptionStatus`, `PlanCatalog`, `IEntitlementService`, `[RequiresFeature]`, limites y plan efectivo por request. Migracion `EntitlementsFase10` aplicada en DEV. |
| 1.1 | Cablear gates a features | Hecho | Gates en Financials, Tandas/Raffles, POS, C.A.M.I., Facebook import/deduplicacion, Exports, GPS en vivo, optimizacion de rutas y `MaxDrivers`. Links publicos siguen sin gate. |
| 1.2 | Ciclo de vida de prueba + alta + bloqueo | Hecho | `POST /api/business` crea Business y membership Owner con `Trialing(Pro)` 14d sin tarjeta. `GET /api/business/account-status` / `subscription/status` devuelve estado para banner/muro. `SubscriptionLockMiddleware` da 402 a endpoints autenticados bloqueados y permite links publicos por token. Trial/PastDue vencidos recalculan perezosamente a `Expired`. Cambio de plan con upgrade inmediato y downgrade pendiente al fin de periodo. Migracion `SubscriptionLifecycleFase12` aplicada en DEV. |
| 1.3 | Suscripcion con Mercado Pago (`preapproval`) | Hecho | Credenciales de plataforma (no per-tenant) en `Platform:MercadoPago`. `PlanCatalog` con precios 129/250/460 MXN y descuentos trimestral -10% / anual -20%. `IMercadoPagoSubscriptionService` crea/actualiza/cancela con auto_recurring y start_date = fin del trial. `SubscriptionController` expone `GET preapproval/public-key`, `GET pricing`, `POST/PUT/DELETE preapproval`. Upgrade inmediato ajusta monto y reactivacion si estaba bloqueada; downgrade aplica periodicidad ya y programa plan al fin del periodo. Cancelacion deja `SubscriptionStatus=Canceled` con `CancellationEffectiveAt = CurrentPeriodEndsAt`. Webhook de plataforma extendido en `PaymentsWebhookController` valida firma x-signature (HMAC-SHA256 sobre `id:..;request-id:..;ts:..;`), acepta `preapproval` y `authorized_payment`: `authorized`/`approved` -> Active, `cancelled` -> Canceled, `paused`/`rejected`/`failed` -> PastDue (dispara gracia 1.2). Migracion `SubscriptionMpPreapprovalFase13` aplicada en DEV. |

## Validacion 1.0

- `dotnet build` -> 0 errores, warnings baseline.
- `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 36/36 PASS.
- Migracion DEV aplicada: `20260625150955_EntitlementsFase10`.
- HTTP real: login DEV y `GET /api/orders/paged` OK.

## Validacion 1.1

- `dotnet build --no-incremental` -> 0 errores, warnings baseline.
- `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 41/41 PASS.
- HTTP real con tenant Entrada: Financials -> 402 `feature_locked`.
- Pedido creado en Entrada y `GET /api/pedido/{accessToken}` sin JWT -> 200.
- `MaxDrivers`: primera ruta OK; siguiente `POST /api/routes` -> 402 con `limit=1`.

## Validacion 1.2

- `dotnet build --no-incremental` -> 0 errores; warnings existentes del proyecto.
- `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 47/47 PASS.
- Migracion DEV aplicada: `20260625154338_SubscriptionLifecycleFase12`.

E2E principal contra API local + base DEV:

- `POST /api/auth/register` con cuenta nueva sin memberships -> OK.
- `POST /api/business` -> 201, Owner creado, `Trialing/Pro`, `locked=False`, `days=14`.
- `PUT /api/business/subscription/plan` durante trial a `Elite` -> OK; `planTier=Elite`, `effectivePlan=Pro`.
- `POST /api/orders/manual` -> OK; `GET /api/pedido/{accessToken}` -> 200.
- Se forzo `TrialEndsAt` al pasado solo para ese Business de prueba.
- `GET /api/business/account-status` -> `Expired/Bloqueado`, `locked=True`, `days=0`.
- `GET /api/orders/paged` autenticado -> 402 `subscription_locked`.
- Cambio de plan estando bloqueado (`Entrada`) -> OK, sigue `effectivePlan=Bloqueado` hasta cobro 1.3.
- `GET /api/pedido/{accessToken}` despues del bloqueo -> 200.

E2E suplementario de links publicos:

- Se creo otro Business trial, 2 pedidos, una ruta y una tanda.
- Tras vencer la prueba: `GET /api/driver/{driverToken}` -> 200, `GET /api/public-tanda/{token}` -> 200, `GET /api/pedido/{accessToken}` -> 200.
- En el mismo tenant bloqueado, `GET /api/orders/paged` autenticado -> 402 `subscription_locked`.

## Validacion 1.3

- `dotnet build --no-incremental` -> 0 errores; warnings preexistentes del proyecto.
- `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 95/95 PASS (47 baseline + 48 nuevos de 1.3).
- Migracion DEV aplicada: `20260625173942_SubscriptionMpPreapprovalFase13`.

Cobertura nueva de tests:
- `PlanCatalogPricingTests` (17): precios base, descuentos 10/20%, descuentos custom, parseo de periodicidad (ES/EN/numerico).
- `MercadoPagoSubscriptionServiceTests` (8): create/update/cancel/get con HttpClient grabado, 404, access-token faltante, validacion de firma x-signature valida/invalida/inexistente.
- `SubscriptionControllerTests` (9): create con trial fresco activa el preapproval, idempotente con preapproval existente, upgrade desde locked/reactiva, upgrade inmediato, downgrade programado, cancelacion, public-key, pricing, error 502 si MP falla.
- `SubscriptionMpWebhookTests` (7): `preapproval authorized` -> Active, `preapproval cancelled` -> Canceled con effective_at, `authorized_payment approved` -> extiende periodo, `rejected` -> PastDue, preapproval desconocido -> 200 sin cambios, firma invalida con secret real -> 401, firma valida -> 200.

E2E contra API local + base DEV:
- `GET /api/business/subscription/pricing` -> Entrada 129/348.30/1238.40, Pro 250/675/2400, Elite 460/1242/4416 (MXN).
- `GET /api/business/subscription/preapproval/public-key` -> `{"publicKey":"dummy"}` (la de plataforma, no del tenant).
- `POST /api/business/subscription/preapproval` con token MP `dummy` -> 502 con mensaje limpio `Mercado Pago respondio HTTP 401`.
- Validacion de input: plan invalido (400), periodicidad invalida (400), email invalido (400).
- Webhook de plataforma sin firma + secret `dummy` -> 200 (DEV); con firma invalida + secret real -> 401.
- Webhook de plataforma con `preapproval` desconocido en MP -> 200, sin tocar ningun Business.
- Webhook de pagos one-time (`type=payment`) -> sigue funcionando igual que antes.

## Validacion FE-0

- `dotnet build --no-incremental` -> 0 errores; warnings preexistentes del proyecto.
- `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 113/113 PASS (95 baseline + 18 nuevos de FE-0).
- Migracion DEV aplicada: `20260625175908_BrandFaseFE0` (agrega LogoUrl/BannerUrl/BrandPrimaryColor/BrandAccentColor a Businesses, default `#6C4AE0`).

Cobertura nueva de tests:
- `BrandControllerTests` (18): `/me` devuelve marca + suscripcion + features, marca vacia en tenant locked, subida de logo guarda URL en el business correcto y NO en otro, rechazo por tamano (2MB logo), rechazo de tipo no-imagen, rechazo de archivo vacio, PUT normaliza hex a MAYUSCULAS, rechazo de hex invalido, accent se puede limpiar, rechazo de nombre > 150, validador hex con matriz de casos validos/invalidos, color rosa `#FF0072` del seeder.

E2E contra API local + base DEV:
- `GET /api/business/me` -> trae `brand.brandPrimaryColor = "#FF0072"` (rosa de Regi Bazar del seeder), 17 features del plan Elite.
- `PUT /api/business/brand` con `brandPrimaryColor: "rojo"` -> 400 con mensaje claro.
- `PUT /api/business/brand` con `brandAccentColor: "#000000"` -> 200; `GET /me` lo refleja.
- `POST /api/business/brand/logo` con PNG valido pero Cloudinary dummy -> 502 con mensaje limpio.
- `POST /api/business/brand/logo` sin archivo -> 400.
- `POST /api/business/brand/banner` con `text/plain` -> 400 "Solo png, jpg o webp".
- `GET /me` sin auth -> 401.
- `PUT /brand` como Driver -> 403 (policy `Admin` lo bloquea, `BusinessMember` lo deja entrar a `/me`).

`IEntitlementService.GetEnabledFeaturesAsync()` agregado y usado por `/me` para devolver las features del plan efectivo.

## Fase 1 - Frontend Web Admin

| # | Plan | Estado | Notas |
|---|---|---|---|
| FE-0 | Kit de marca por tenant | Hecho | `Business.LogoUrl/BannerUrl/BrandPrimaryColor/BrandAccentColor`, `POST /api/business/brand/{logo,banner}` (multipart, 2MB/5MB, png/jpg/webp) y `PUT /api/business/brand` (name + colores hex). `GET /api/business/me` devuelve marca + suscripcion + features en una llamada. Sin gate por plan: la marca esta en TODOS. Seeder DEV: Business #1 arranca con rosa `#FF0072` (Regi Bazar). |
| FE-1 | Registro / Acceso | Hecho | `AuthService` reescrito: signals para token/accountId/displayName/memberships/activeBusinessId. Auto-seleccion de business unico; multi-tenant se conserva el actual si sigue valido. `auth.interceptor` añade `X-Business-Id` a `/api/**` y omite rutas publicas por token (`/pedido/`, `/repartidor/`, `/tanda-view/`, `/live/`). `error.interceptor` limpia sesion en 401 sin pisar mensajes del propio `/auth/`. `LoginComponent` rediseñado con 3 tabs (telefono OTP con modo DEV visible, Facebook stub, correo legacy "acceso de equipo"). Tras login: si Account sin Owner membership -> `/onboarding` (placeholder FE-2); si Owner/Admin -> `/admin`; Driver -> `/admin/routes`; Scaner -> `/pos`. |
| FE-2 | Alta de negocio + marca inicial | Hecho | `BrandService` nuevo: createBusiness, getMe, updateBrand, uploadLogo, uploadBanner. `OnboardingWizardComponent` con stepper de 3 pasos (Datos / Marca / Listo) + vista previa en vivo de la cabecera del panel con el color, logo, banner y nombre. Logo y banner opcionales; el alta no se bloquea por no subirlos. Al crear: setea activeBusinessId, refresca memberships locales, lee `/me` para mostrar dias restantes de la prueba Pro. Login y `index.html` title reescritos a "Neni's App" (la plataforma) — la marca de cada tenant sigue siendo per-tenant via FE-0. |
| FE-3 | Planes + estado + muro | Hecho | Página `/admin/subscription` con estado actual, planes (Entrada/Pro/Elite) y periodicidad (mensual/trimestral/anual con descuentos que entrega el backend). `SubscriptionService` (account-status, pricing, changePlan, preapproval CRUD). `BusinessBootstrapService` carga `/me` + `/account-status` en paralelo y expone signals (isLocked, planTier, pendingPlan, daysLeft, etc.) con catálogo de features. `SubscriptionBannersComponent` arriba del top-bar (trial/past-due/pending plan) y `SubscriptionPaywallComponent` como overlay full-screen cuando la cuenta está bloqueada (solo Owner/Admin; Driver/Scaner siguen). `subscriptionGuard` redirige al muro menos a `/admin/subscription`. `error.interceptor` ahora en 402 refresca bootstrap y navega a `/admin/subscription`. Item "Mi Plan" en el sidebar. `ng build` → 0 errores TS. |
| FE-4 | Pago con brick de MP | Hecho | `MercadoPagoBrickService` carga `https://sdk.mercadopago.com/js/v2` (idempotente) y la PublicKey de PLATAFORMA desde `/api/business/subscription/preapproval/public-key` (nunca la del tenant). Página `/admin/subscription/checkout?plan=X&periodicity=Y` con resumen editable, email del titular, brick de tarjeta (`cardForm` con placeholders en español) y POST al endpoint de 1.3 con `cardTokenId` + `payerEmail`. En éxito: confeti (`canvas-confetti`) + refresh del bootstrap + pantalla final con link al panel. Errores de MP se muestran sin tono de disculpa. `/admin/subscription` ahora distingue dos flujos: si hay preapproval activo, "Elegir {plan}" usa `updatePreapproval` (sin pedir tarjeta; upgrade inmediato / downgrade a fin de periodo); si está bloqueado o sin preapproval, navega al checkout. Nueva sección "Administrar suscripción" con "Cambiar tarjeta o plan" (→ checkout) y "Cancelar" (`DELETE /preapproval`). Paywall CTA principal "Pagar y entrar" va directo al checkout. `ng build` → 0 errores TS. |
| FE-5 | Panel temado + editor de marca + gating | Hecho | `ThemeService` aplica la marca del business (`brandPrimaryColor` + `brandAccentColor`) como CSS custom properties (`--brand-primary-50..900`, `--brand-primary`, `--brand-accent`) en `:root`, derivando 9 tonos via HSL. Reacciona en vivo al `BusinessMeDto` y se re-aplica al cambiar `activeBusinessId`. Layout admin refactorizado para consumir esas variables (sidebar, avatar, accents, fondo) y muestra el logo/nombre real del tenant en el top bar y sidebar. Hero del dashboard muestra banner + logo + nombre del negocio. Nueva página `/admin/brand` (Owner/Admin) con nombre, color principal + acento (picker y hex), upload de logo (2MB) y banner (5MB) con preview en vivo, validacion de formato/tamaño y guardado via endpoints de FE-0. Gating por plan: nav items de features Pro/Elite (Tandas/Sorteos, Finanzas, Reportes, C.A.M.I.) aparecen con candado 🔒 y tooltip "requiere {plan}"; click → `UpsellModal` con CTA a `/admin/subscription`. `BusinessSwitcherComponent` en el top bar aparece si el Account tiene varias memberships y al cambiar re-temea el panel. `ng build` → 0 errores TS. |

## Migracion

| # | Plan | Estado | Notas |
|---|---|---|---|
| M.1 | Migrador consola una sola vez | Hecho | Proyecto `EntregasApi.Migrator` (.NET 8 console) + `EntregasApi.Migrator.Tests`. Modo `--verify` (6 chequeos: conteos+tokens+spot-check+secuencias+FKs+identidad) y modo copia (una sola transaccion, todo o nada). Sesion de origen forzada a `READ ONLY` con `SET default_transaction_read_only=on`. Pre-chequeo: aborta si destino ya tiene Business Id=1. Transformaciones A-F: crea Business Id=1 con marca Regi Bazar (`#FF0072` rosa, PlanTier=Elite), `Users` -> `Accounts`+`Memberships` (PKs conservadas, roles Admin/Driver/Scaner), `CashRegisterSession.UserId` -> `AccountId`, estampa `BusinessId=1` en toda tabla tenant-ownada, `Clients.AccountId=NULL`, `AppSettings` con la fila unica de origen. `MercadoPagoAccessToken` se encripta con `DataProtectionProvider` (`ApplicationName="EntregasApi"`, mismo protector que la app) o queda NULL con WARN. `evidence-map` JSON opcional para reescribir las 3 `DeliveryEvidences` con ruta local. Reset de secuencias int-PK al `MAX(Id)` (Guid-PK no llevan). Respeta las 4 colisiones criticas: `products` (Tanda) != `Products` (POS), `payments` (TandaPayment) != `OrderPayments`, `tandas` snake_case != `Orders` PascalCase. README con orden de invocacion (ensayo en branch Neon -> corte real -> rollback). NO se ha corrido contra datos reales: el ensayo va contra branch de Neon (copia), nunca produccion, segun el plan. |
| M.1-RUNBOOK | Runbook operativo simplificado | Hecho | `EntregasApi.Migrator/M.1-RUNBOOK.md`. 8 pasos lineales, sin "Etapa 5/6" ni "ventana sin live": (1) crear 2 branches Neon + correr `dotnet ef database update` en cada uno con env var `ConnectionStrings__Default`, (2) `--verify` sobre ensayo, (3) copia sobre ensayo, (4) re-verify, (5) smoke test manual, (6) promover a `prod-cutover` (repetir 2-4), (7) apuntar la app (env var + reinicio), (8) reversión = re-deploy normal. Apéndices: decisiones, psql rápido, lo que NO hace. Las 3 evidencias con ruta local eran pruebas locales — no se rescatan, no se usa `--evidence-map`. |
| M.1-EJECUCION | Corrida real del migrador | Hecho | Corrida one-shot ejecutada el 2026-06-26 contra `ep-steep-bar-ai7vx9g2` (origen, prod single-tenant) -> `ep-lively-wind-aty96jj0` (destino, prod-cutover multi-tenant vacio) y `ep-polished-brook-ata0c0us` (destino, staging-cutover para ensayo). Modo `--preflight` agregado al migrador para validar accesibilidad y estado sin escribir. 9 migraciones nuevas EF aplicadas a cada destino (0.1 a 1.3 + FE-0). Copia con `--rb-mp-token` produjo: Business=1 (Regi Bazar rosa #FF0072, Plan=Elite, Status=Active), Accounts=4, Memberships=4 (1 Owner/1 Driver/2 Scaner), Orders=743, Clients=334, OrderItems=2782, OrderPayments=656, AppSettings=1, CashRegisterSessions=1, products=4 (Tanda), payments=255, tandas=6, raffles=1, raffle_entries=235, etc. `--verify` post-copia: **6/6 PASS** (conteos 35 tablas OK, tokens 0 nulos/0 dupes identicos, spot-check 4/4, secuencias, 0 huerfanos, identidad OK). ORIGEN 100% intacta: 16/16 snapshots coinciden (Orders=743, Clients=334, Users=4, __EFMigrationsHistory=38). `MercadoPagoAccessToken` encriptado en destino (prefijo `CfDJ8GTIpkO...`, 219 chars, NO en texto plano). Bugs del codigo encontrados y arreglados: 15 (case-fold, int->long cast, OrderPackages mal clasificado uuid, pkey dinamica por tabla, `pg_sequences` case-insensitive, huérfanos prefijo tabla, separacion selectColumns/copyColumns, BeginBinaryImport sin opciones, tipos Postgres inferidos del reader, orden topologico de raffle_entries, copia custom de CashRegisterSessions, ChatMessages/ClientMergeAudits sin BusinessId, Memberships.Role como int, RemapCashRegisterUserId redundante, secuencias case-insensitive en Verify). |
| M.1-APPSETTINGS | Cambio del connection string en app | Hecho | `appsettings.json` actualizado: `Default` apunta ahora a `ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech` (prod-cutover nuevo). Password se mantiene como `dummy` (placeholder) por la convencion del repo (el password real viene de env var `ConnectionStrings__Default` en el host de despliegue, o del `appsettings.Development.json` que SI esta en `.gitignore`). El connection string viejo (`ep-steep-bar-ai7vx9g2` en `c-4`) queda como referencia historica pero ya no es el destino activo. |
| M.1-SMOKE-TEST | Smoke test automatizado post-cutover | Hecho | `EntregasApi.Migrator/smoke-test.ps1` (PowerShell) + `M.1-SMOKE-TEST.md` (instrucciones). 7 tests: 4 publicos (pedido, driver, tanda, token-invalido) + 3 autenticados (login, business/me, orders/paged) que requieren `SMOKE_OWNER_EMAIL` y `SMOKE_OWNER_PASSWORD` via env vars. Validado localmente con la API corriendo contra prod-cutover: 4/4 PASS en los tests publicos. Endpoint de login responde 401 con password incorrecta (la cuenta existe, el endpoint funciona). Exit code 0 = todo OK, 1 = algun FAIL. |
| M.1-POST-CUTOVER | Pasos restantes operativos | Hecho | `EntregasApi.Migrator/M.1-POST-CUTOVER.md`. 10 pasos en orden: (1) verificar pre-condiciones, (2) apuntar app + reiniciar, (3) smoke test automatizado, (4) smoke test manual en navegador, (5) verificar DataProtection keys (critico si los hosts son distintos), (6) avisar a Regi, (7) monitoreo 24-72h, (8) limpieza de archivos y seguridad, (9) archivar base vieja despues de 30 dias, (10) opcional campana de captura de telefonos. Tiempo total ~30 min de trabajo activo + monitoreo pasivo. Incluye tambien el procedimiento de rollback express (5 min: cambiar env var al host viejo + reiniciar). |

## Resumen

- Hechos: 22 (0.0 -> 1.3 + FE-0..FE-5 + M.1 + M.1-RUNBOOK + M.1-EJECUCION + M.1-APPSETTINGS + M.1-SMOKE-TEST + M.1-POST-CUTOVER).
- Pendientes: 0 en codigo. Pendientes operativos del usuario (seguid `M.1-POST-CUTOVER.md`): reiniciar la app, correr el smoke test, smoke test manual, verificar DataProtection, notificar a Regi, monitorear 24-72h, limpiar archivos, archivar base vieja en 30 dias.

## Convenciones Del Repo

- Tests: `dotnet test Tests\EntregasApi.Tests\EntregasApi.Tests.csproj` -> 113/113 verde (backend sin cambios nuevos en este paso).
- Tests del migrador: `dotnet test EntregasApi.Migrator.Tests/EntregasApi.Migrator.Tests.csproj` -> 18/18 PASS (10 del parser CLI + 8 del plan).
- Frontend build: `npx ng build` -> 0 errores TS.
- Frontend dev: `npx ng serve` levanta en `http://localhost:4200`.
- Build: `dotnet build` -> 0 errores, warnings preexistentes.
- Migrador: `dotnet build EntregasApi.Migrator/EntregasApi.Migrator.csproj` -> 0 errores, 0 warnings. Modo `--verify` (sin escribir) y modo copia (1 transaccion).
- Migraciones: `dotnet ef database update --project EntregasApi.csproj`.
- Connection string DEV: `appsettings.Development.json` (Neon branch de desarrollo, no produccion).
- Stack: .NET 8 / EF Core 8 / PostgreSQL 17 (Neon) / SignalR.
- Credenciales de MP:
  - Plataforma (cobro suscripcion vendedoras) en `Platform:MercadoPago:AccessToken/PublicKey/WebhookSecret`.
  - Per-tenant (cobro clientas) sigue en `MercadoPago:AccessToken` y `Business.MercadoPagoAccessToken` (encriptado).
- Regla del plan: validar antes de avanzar al siguiente paso.
