Kit de construcción — Plataforma SaaS **Neni's App** (Regi Bazar → multi-tenant)
Qué es esto: todos los prompts para Claude Code, en orden, desde los cimientos hasta la Fase 1 frontend, más el migrador. Es el plan de construcción completo de lo que diseñamos.
La plataforma se llama **Neni's App**. Donde aparezca marca de plataforma (login, marketing, PWA shell, titulo de la ventana, manifest) usar "Neni's App". La marca de CADA tenant (Regi Bazar, Tienda Demo, etc.) sigue siendo per-tenant y se controla via FE-0.
Cómo usar este kit (léelo una vez)

1. Un bloque a la vez. Pega UN prompt en Claude Code, deja que lo ejecute, y no le des el siguiente hasta haber probado el actual.
2. Cada prompt termina con `── ALTO Y VALIDA ──`. Esa es la orden para que Claude Code se detenga, compile, valide que no hay errores, te diga qué probar y espere tu OK. No la quites.
3. Orden = tu runbook. Cimientos (Fase 0) → Monetización backend (Fase 1) → Frontend admin (Fase 1) → Migrador. El migrador se ejecuta hasta el corte (Etapas 5–6 de tu checklist), pero está escrito aquí por completitud.
4. Base de datos: todo el desarrollo corre contra una base de desarrollo (un branch de Neon o una base nueva vacía). La base de producción de tu esposa no se toca hasta el corte, con el migrador.
5. Stack: backend `sellgeneral-api` (`C:\Codigos\sellgeneral-api`, .NET 8 / EF Core 8 / PostgreSQL-Neon / SignalR); frontend `sellgeneral` (`C:\Codigos\sellgeneral`, Angular 21, standalone + SSR + PWA + Capacitor 8, Tailwind 4). Los repos ORIGINALES (`regibazar-web` y `api/EntregasApi`) están **congelados y no se tocan**.
Plantilla del checkpoint (incrustada en cada prompt, adaptada al stack):

* Backend: `dotnet build` sin errores ni warnings nuevos · migraciones aplicadas a base de DEV · tests del paso en verde.
* Frontend: `ng build` sin errores TS · `ng serve` carga la vista sin errores en consola.
* Siempre: DETENTE, resume qué cambió y qué/dónde probar, espera el OK antes del siguiente paso.
FASE 0 — Cimientos (backend)
Convierten el sistema single-tenant en multi-tenant con identidad unificada, sin features nuevas. Es la parte más delicada y va primero.
0.0 — Sincronizar CLAUDE.md
Barato y primero: Claude Code trabaja mejor con contexto real.

```
Actualiza CLAUDE.md para que refleje la REALIDAD del repo (hoy está desactualizado):
- Frontend Angular 21.2.1 (standalone + SSR/Express + PWA + Capacitor 8), NO Angular 18.
- Base de datos en Neon (PostgreSQL 17), NO Render.
- JWT de 7 días SIN refresh token (no existe lógica de refresh).
- El Role NO viaja en el JWT (claims solo userId/email/name); el control de roles hoy es solo en el frontend.
- TTS principal ElevenLabs, con Google Cloud TTS como fallback. Transcripción con OpenAI Whisper.
- Nomenclatura de tablas MIXTA: PascalCase (Orders, Clients) y snake_case (tandas, raffle_entries); "TandaProduct" mapea a la tabla "products" (≠ "Products" del POS) y "TandaPayment" a "payments" (≠ "OrderPayments").
Agrega una sección "Single-tenant hoy" que diga explícitamente: ninguna entidad tiene BusinessId/TenantId; el negocio está hardcodeado (nombre "Regi Bazar" en prompts de Gemini, dominio regibazar.com en CORS, depot fijo en Nuevo Laredo).
No cambies código.

── ALTO Y VALIDA ──
Solo es documentación, no compila nada. Confirma que CLAUDE.md describe el repo real y DETENTE para que Eduardo lo revise antes de seguir.

```

0.1 — Identidad unificada + tenancy
El cambio estructural grande: Account (persona) + Business (tenant) + Membership (rol por relación).

```
Refactoriza la identidad y agrega multi-tenancy al backend EntregasApi (.NET 8 / EF Core 8 / PostgreSQL). NO borres la tabla User en este prompt (el login sigue sobre ella hasta el 0.2); aquí solo agregamos el modelo nuevo y migramos en paralelo.

1) Account (la PERSONA, identidad global única por humano): Id, DisplayName, ProfilePhotoUrl (null), Phone (null, normalizado, unique cuando no es null), FacebookUserId (null, app-scoped, unique cuando no es null), Email (null), PasswordHash (null, BCrypt, solo cuentas legacy admin/conductor), CreatedAt. Constraint: al menos un método presente (Phone, FacebookUserId o Email).

2) Business (el TENANT): Id, Name, Slug (unique), City, FrontendUrl, DepotLat, DepotLng, GeocodingRegion (default "Nuevo Laredo, Tamaulipas, MX"), GeminiBusinessName, MercadoPagoAccessToken (ENCRIPTADO; token de la VENDEDORA, no global), PlanTier (string, default "Entrada"), IsActive, CreatedAt.

3) Membership (la RELACIÓN persona↔negocio con rol): Id, AccountId (FK), BusinessId (FK), Role (enum: Owner, Admin, Driver, Scaner), CreatedAt. Unique (AccountId, BusinessId). Aquí vive "soy Owner de éste y clienta de otros": el rol es POR relación, no global.

4) Agrega AccountId (FK a Account, NULLABLE) a Client. Una Account ↔ muchas Client (una por cada vendedora que le vende); una Client ↔ 0 o 1 Account. Null = clienta anónima creada por la vendedora (estado actual, debe seguir funcionando).

5) Agrega BusinessId (FK, REQUERIDO) a las raíces: Client, DeliveryRoute, Product, CashRegisterSession, Supplier, SalesPeriod, AppSettings (deja de ser singleton Id=1: una fila por Business), TandaProduct, Tanda, Raffle, LoyaltyReward, LiveSession, FcmToken, PushSubscriptions. Denormaliza BusinessId también a hijas con endpoint propio: Order, OrderItem, OrderPayment, OrderPackage, Delivery, DeliveryEvidence, ClientAlias, LoyaltyTransaction, TandaParticipant, TandaPayment, RaffleParticipant, RaffleEntry.

6) ICurrentTenant (expone ActiveBusinessId, lo resuelve el 0.2) e ICurrentAccount (la Account autenticada), inyectables.

7) DOS patrones de query (CRÍTICO, no los mezcles):
   - Entidades de negocio: HasQueryFilter(e => e.BusinessId == _tenant.ActiveBusinessId).
   - Lecturas de clienta (cross-tenant: histórico, vendedoras seguidas): filtradas por AccountId, DEBEN usar IgnoreQueryFilters() + scoping explícito por AccountId. Documenta esto para no romper el lado clienta.

8) Migración EF + seeder de DESARROLLO idempotente. IMPORTANTE: este seeder corre SOLO en entorno Development (o tras un flag --seed-dev), NUNCA en producción. (En producción, la base nueva recibe los datos reales vía el MIGRADOR en el corte, que aborta si Business #1 ya existe. Si el seeder corriera en prod, chocaría con el migrador.)
   - Crea Business #1 = "Regi Bazar" (slug "regibazar", coords reales, PlanTier="Elite").
   - Por cada User existente: crea una Account (copiando Email/PasswordHash) y una Membership(Account, Business#1, Role) mapeando User.Role → enum: "Admin"→Owner, "Driver"→Driver, "Scaner"→Scaner.
   - Asigna BusinessId=1 a TODAS las filas existentes. Client.AccountId queda null (nadie reclamado aún).

Gemini/Maps/Cloudinary/ElevenLabs siguen GLOBALES; solo Mercado Pago es per-tenant. NO toques auth ni SignalR aquí.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores, 0 warnings nuevos.
• `dotnet ef migrations add IdentidadYTenancy` y `dotnet ef database update` contra base de DESARROLLO (nunca la de Eduardo).
• En Development, el seeder deja Business #1 + 4 Accounts + 4 Memberships + todo con BusinessId=1.
• DETENTE. Resume qué cambió y qué revisar. Espera el OK de Eduardo.

```

0.2 — Auth sobre Account + autorización server-side
Cierra la fuga entre tenants: el rol viaja en el token y se valida en el backend, no solo en Angular.

```
Cambia la autenticación para que corra sobre Account (no User) y endurece la autorización. Al terminar, retira la tabla User (ya migrada en 0.1).

1) Account soporta TRES logins: (a) Email+Password (BCrypt) para cuentas legacy; (b) Teléfono (deja endpoint y shape listos para OTP; el proveedor SMS se integra después); (c) Facebook Login (permiso public_profile, guarda el id app-scoped en FacebookUserId). Un Account puede tener varios enlazados.

2) JWT: claim sub = AccountId. Incluye las Memberships (o resuélvelas por request). "Negocio activo": para requests admin/conductor se determina por header X-Business-Id, VALIDADO contra las Memberships del caller (solo puede actuar donde tiene Membership). ICurrentTenant.ActiveBusinessId sale de ahí, en middleware, ANTES del query filter de EF.

3) Autorización real: [Authorize] por defecto + policies por rol evaluadas contra la Membership en el negocio activo (no rol global). Ej: policy "Driver" = el caller tiene Membership Role=Driver en ActiveBusinessId. Replica lo que hoy SOLO hace el authGuard de Angular.

4) Endpoints públicos por token (Order.AccessToken, DeliveryRoute.DriverToken, Tanda.AccessToken, LiveSession.Id): SIGUEN SIN LOGIN. Resuelve el tenant desde el BusinessId de la fila dueña del token. NO los rompas: mantienen vivo el link para la señora no registrada.

5) Endpoints de clienta: autenticados, filtrados por AccountId, cross-tenant (patrón IgnoreQueryFilters del 0.1).

6) Verifica: un Account del Business A recibe 403/404 (no datos) al pedir recursos de negocio del Business B vía API directo, aunque manipule X-Business-Id.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Migración para eliminar la tabla User + `database update` en DEV.
• Test de aislamiento: con dos tenants de DEV, un token del A NO obtiene datos del B vía API directo (no solo UI).
• DETENTE. Resume y espera el OK. (Este paso y el 0.3 son los que pueden filtrar datos: máxima atención.)

```

0.3 — Reclamar perfil
El corazón de la identidad unificada: enlazar la Account global con el/los registro(s) Client que cada vendedora ya tiene.

```
Implementa el flujo de "reclamar perfil": enlazar una Account global con el/los Client que las vendedoras ya tienen de esa persona.

1) Endpoint POST para enlazar: setea Client.AccountId = Account actual. NUNCA enlaces sin una señal de prueba. Enlace sin prueba = robo de identidad / fuga de datos.

2) Camino principal (token-seeded): app abierta desde un link con Order.AccessToken → resuelve la Client dueña → "¿este pedido es tuyo? reclámalo" → enlaza. Llegar por el token ES la prueba.

3) Camino secundario (fan-out por teléfono): tras registrarse, busca TODAS las Client (cross-tenant, IgnoreQueryFilters) cuyo NormalizedPhone coincida con Account.Phone y ofrécelas para reclamar una por una. El teléfono es la llave fuerte. NO auto-enlaces por Facebook: FacebookProfileUrl (lo que guardó la vendedora) NO coincide con el id app-scoped de Login; úsalo solo como pista suave que el usuario confirma a mano. (Nota de datos reales: hoy solo ~4% de las Client tienen teléfono y 0% Facebook; el 95% solo tiene nombre único. Por eso el camino principal es el TOKEN, no el teléfono.)

4) Guard rails: el match por teléfono idealmente exige OTP antes de enlazar. Reusa Normalized* y ClientAlias/ClientMergeAudit (hoy deduplican DENTRO de un negocio; aquí extiéndelos para empatar identidad global ↔ registro local de cada vendedora).

5) Una Account puede quedar enlazada a N Client (una por vendedora). Reclamar una NO debe exponer datos de otra vendedora: cada Client sigue siendo de su Business; el histórico cross-tenant se arma juntando SUS Client enlazadas, filtrado por AccountId.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores. Tests del flujo (enlace por token OK; enlace sin prueba RECHAZADO; reclamar una Client no expone otra).
• DETENTE. Resume y espera el OK.

```

0.4 — Aislar SignalR por tenant
Que el GPS/pedidos/POS de un negocio jamás lleguen a conexiones de otro.

```
Aísla los 5 hubs de SignalR (DeliveryHub, TrackingHub, OrderHub/OrdersHub, LogisticsHub, PosHub) por tenant.

1) Prefija TODO nombre de grupo con el tenant: "t{BusinessId}_...".
2) Resuelve el BusinessId de la conexión: para admin/conductor con JWT, desde el negocio activo (claim/validación de 0.2, pasado por query string ?access_token=); para clientas/conductor SIN JWT, desde el token de recurso de la URL (Order.AccessToken / DriverToken).
3) Arregla las inconsistencias detectadas en el inventario: unifica "Order_" vs "order_" y "Route_{driverToken}" (Delivery) vs "Route_{routeId}" (Logistics) en una convención única, ya prefijada por tenant.
4) Elimina el riesgo de colisión del grupo de POS "order_{orderId}" (int): dos tenants con el mismo Id de orden colisionan → prefíjalo con tenant.
5) Asegura que un evento de ubicación/pedido/POS de un tenant jamás llegue a conexiones de otro.
6) CORS de SignalR: AllowCredentials para los orígenes registrados por tenant + localhost:4200 + capacitor://localhost (se amplía en 0.5).

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Prueba con dos tenants de DEV: un ReportLocation de un tenant solo llega a las conexiones de ESE tenant.
• DETENTE. Resume y espera el OK.

```

0.5 — De-hardcodear identidad + storage por tenant
Saca "Regi Bazar", el dominio, el depot y la región del código; mueve las fotos a carpeta por tenant.

```
Saca del código/appsettings todo lo hardcodeado del negocio único y hazlo per-tenant, leyéndolo desde ICurrentTenant / la entidad Business.

1) Parametriza desde Business (per-tenant): el nombre "Regi Bazar" embebido en los prompts de GeminiService; App:FrontendUrl; los orígenes de CORS; el depot Cami:RouteCenterLat/Lng (usa Business.DepotLat/Lng); y el bias de región de GeocodingService (usa Business.GeocodingRegion).
2) CORS multi-tenant: permite los dominios registrados en Business además de localhost:4200 y capacitor://localhost.
3) Cloudinary por tenant: cambia el folder raíz de "regibazar/{folder}" a "{business.Slug}/{folder}" en CloudinaryService.
4) Retira el storage local /uploads y /uploads/evidence (no sobrevive reinicios del contenedor ni es tenant-safe): toda evidencia/gasto va a Cloudinary. (Las 3 evidencias legacy con ruta local se rescatan en la migración, no aquí.)
5) Quita cualquier seed GLOBAL que ya no tenga sentido multi-tenant (AppSettings singleton Id=1, catálogo RegiPuntos al arranque): esos datos ahora son por-tenant y se crean en el onboarding (Fase 1) o llegan por el migrador. Si los conservas para DEV, guárdalos tras el check de Development del 0.1.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Verifica que un tenant de DEV con otro Slug suba fotos a su propia carpeta y geocodifique con su propia región.
• DETENTE. Cierra Fase 0. Resume y espera el OK.

```

FASE 1 — Monetización (backend)
Define los planes, los valida server-side, gobierna la prueba/bloqueo y cobra la suscripción de la vendedora.
1.0 — Motor de entitlements (plan → features)
Sin Mercado Pago todavía. Solo qué desbloquea cada plan, validado en el backend.

```
Implementa el motor de entitlements en EntregasApi (.NET 8). Define qué desbloquea cada plan y lo valida SERVER-SIDE. NO integres Mercado Pago aquí (va en 1.3).

1) Enum Feature con todas las features gateables:
   ManualOrders, ClientDirectory, PublicTrackingLink, OrderStatusPush, ClientAccount, Loyalty,  // base
   LivePush, LiveGpsTracking, Financials, TandasRaffles, Pos, FacebookImport, VipDrops,          // pro
   CamiAssistant, TrafficRouteOptimization, Exports, PrioritySupport                             // elite
2) Enum LimitKey: MaxDrivers, RouteOptimizationCalls.
3) Catálogo de planes EN CÓDIGO (static; documenta que puede migrar a tabla después):
   - "Entrada": las 6 features base. Limits: MaxDrivers=1, motor de ruta=heurístico.
   - "Pro": base + LivePush, LiveGpsTracking, Financials, TandasRaffles, Pos, FacebookImport, VipDrops. MaxDrivers=int.MaxValue.
   - "Elite": todo Pro + CamiAssistant, TrafficRouteOptimization, Exports, PrioritySupport. Todo ilimitado.
   Expón PlanCatalog.Get(planTier) -> { Features (HashSet<Feature>), Limits }.
4) Agrega a Business (que ya tiene PlanTier):
   SubscriptionStatus (enum: Trialing, Active, PastDue, Expired, Canceled), TrialEndsAt (DateTime? UTC), CurrentPeriodEndsAt (DateTime? UTC).
   Migración EF. En el seeder de DEV, el Business #1 queda PlanTier="Elite", SubscriptionStatus=Active (Regi Bazar no paga, es dueña de la plataforma).
5) IEntitlementService (scoped), usa ICurrentTenant. Plan EFECTIVO (CRÍTICO, no leas PlanTier crudo):
   - Trialing y TrialEndsAt>UtcNow -> features de "Pro" (la prueba da Pro).
   - Active -> features de su PlanTier.
   - Trialing y TrialEndsAt<=UtcNow, o Expired, o PastDue tras gracia -> BLOQUEADO: cero features de negocio (ver 1.2).
   Métodos: HasFeature(Feature), GetLimit(LimitKey), EffectivePlanTier(). Evaluación PEREZOSA por request (NO hay scheduler en el proyecto — no agregues uno).
6) Enforcement:
   - Atributo [RequiresFeature(Feature.X)] como IAsyncActionFilter: si !HasFeature -> 402 Payment Required, body { error:"feature_locked", feature:"X", requiredPlan:"Pro|Elite" }.
   - Fuera del borde de controller (push de EN VIVO, alta de repartidor N+1) usa IEntitlementService directo. Helper EnsureWithinLimit(LimitKey, currentCount).
7) Tests: Trialing ve features Pro; vencido da 402; Active Entrada NO ve Financials; MaxDrivers respeta el límite.
NO toques los 20 controllers todavía (eso es 1.1).

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores. Migración aplicada en DEV. Tests del punto 7 en verde.
• DETENTE. Resume y espera el OK.

```

1.1 — Cablear los gates a las features reales
Conecta el motor a los endpoints/servicios que ya existen.

```
Aplica los gates del motor (1.0) a los endpoints/servicios reales, con este mapeo. [RequiresFeature] en controllers, IEntitlementService en servicios.
- AdminFinancialsController -> Financials (Pro).
- TandaController + RafflesController -> TandasRaffles (Pro). PublicTandaController (vista pública por token) NO se gatea.
- PosController + lógica de CashRegisterSession -> Pos (Pro).
- CamiController y todo uso de CamiService como asistente contextual -> CamiAssistant (Elite). Los otros usos de GeminiService que NO son el asistente NO se gatean aquí.
- ClientsController: importación masiva de Facebook + flujo de deduplicación -> FacebookImport (Pro).
- Push de "EN VIVO"/"mercancía nueva" a seguidoras -> LivePush (Pro). El push de ESTATUS del pedido (OrderStatusPush) es base, NO se gatea.
- GPS en vivo a la clienta (grupo Tracking_ / LocationUpdate de SignalR) -> LiveGpsTracking (Pro). El orden de paradas y el mapa base NO se gatean.
- RouteOptimizerService: si HasFeature(TrafficRouteOptimization) (Elite) usa Google Routes API; si NO, usa el FALLBACK HEURÍSTICO de vecino-más-cercano que YA existe en ese servicio. La optimización nunca se "quita": se degrada de motor por plan. EnsureWithinLimit(RouteOptimizationCalls) solo en el camino Google.
- Alta de repartidor: antes de crear el repartidor/ruta N+1, EnsureWithinLimit(MaxDrivers, conteo actual). Entrada=1.
- Exportaciones (EPPlus / endpoints de export en Reports) -> Exports (Elite).
SIEMPRE disponible (base, NO gatear): captura manual, directorio de clientas + tags (incl. blacklist privada), link público de rastreo, estatus del pedido, cuenta de clienta, 1 repartidor, ruta heurística, mapa.
Verifica que cada endpoint gateado devuelva 402 (no 403/500) y que las vistas públicas por token sigan funcionando sin importar el plan del tenant.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Prueba: un tenant Entrada recibe 402 en /financials; las vistas públicas por token responden igual.
• DETENTE. Resume y espera el OK.

```

1.2 — Ciclo de vida de la prueba + alta de negocio + bloqueo
Onboarding, prueba de 14 días que da Pro, y el muro de bloqueo. Sin MP todavía.

```
Implementa el ciclo de vida de la suscripción a nivel app, SIN Mercado Pago todavía.
1) Onboarding self-serve: endpoint para que una Account autenticada (de 0.2) cree un Business y se vuelva Owner. Al crearlo: PlanTier="Pro", SubscriptionStatus=Trialing, TrialEndsAt=UtcNow+14d, CurrentPeriodEndsAt=null. Crea Membership(Account, Business, Owner). NO se pide tarjeta aquí.
2) Estado BLOQUEADO (no hay tier gratis donde caer): cuando el plan efectivo cae a bloqueado, el tenant queda en un muro "elige un plan para continuar".
   - Endpoints de negocio del owner/admin: 402 (los gatea el motor).
   - PERO las vistas públicas por token (Order.AccessToken, DeliveryRoute.DriverToken, Tanda.AccessToken) SIGUEN funcionando aunque el tenant esté vencido: leer pedido/rastreo de pedidos YA existentes, sí. Crear pedidos NUEVOS, no (requiere al owner, que está bloqueado).
   - Endpoint GET "estado de cuenta": { effectivePlan, subscriptionStatus, trialEndsAt, isLocked, daysLeft } para el banner/muro del frontend.
3) Gracia: si SubscriptionStatus pasa a PastDue (lo dispara el webhook en 1.3), NO bloquees de inmediato: X días de gracia (config, ej. 3) antes de Expired/bloqueado. Recalcúlalo en la evaluación perezosa.
4) Cambio de plan (estructura; el cobro va en 1.3): endpoint para que el owner elija/cambie plan. Upgrade inmediato; downgrade al fin del periodo (CurrentPeriodEndsAt). Guarda el plan elegido para que 1.3 cree/actualice la suscripción.
NO agregues scheduler: todo vencimiento se evalúa por request en IEntitlementService.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Prueba end-to-end con tenant de DEV: alta → Trialing(Pro) → forzar TrialEndsAt en el pasado → endpoints de negocio dan 402 pero un link público existente sigue abriendo.
• DETENTE. Resume y espera el OK.

```

1.3 — Suscripción con Mercado Pago (preapproval)
El cobro de la mensualidad de la vendedora, con las credenciales de PLATAFORMA (no las de ella).

```
Integra el COBRO de la mensualidad de la vendedora con el producto de Suscripciones (preapproval) de Mercado Pago. IMPORTANTE: verifica la API vigente de preapproval en la doc oficial de MP antes de implementar; los campos/endpoints de suscripciones cambian, no asumas el shape de memoria.

DISTINCIÓN CRÍTICA — dos integraciones MP, NO las confundas:
- Business.MercadoPagoAccessToken (per-tenant): la VENDEDORA cobra a SUS clientas. YA existe. NO se usa aquí.
- Credenciales de PLATAFORMA (tuyas, nuevas: Platform:MercadoPago:AccessToken/PublicKey, global): con estas TÚ cobras a la VENDEDORA. La suscripción se crea SIEMPRE con las de plataforma. Con el token del tenant, la vendedora se cobraría a sí misma: inválido.

1) Al CONVERTIR (día 14, cuando el owner elige plan y mete tarjeta): crea un preapproval con credenciales de PLATAFORMA, monto = precio del plan (Entrada 129 / Pro 250 / Elite 460 MXN, configurables), frecuencia mensual. Captura de tarjeta por el flujo de MP (no almacenes tarjeta tú). Como NO se pidió tarjeta al inicio, la prueba no fue free_trial de MP: la suscripción se crea fresca aquí y empieza a cobrar. Al crear OK: SubscriptionStatus=Active, PlanTier=plan elegido, CurrentPeriodEndsAt=+1 mes, guarda PreapprovalId.
2) Webhook: EXTIENDE PaymentsWebhookController (ya maneja pagos one-time de pedidos/tandas) o agrega uno para eventos de preapproval/authorized_payment, validado con credenciales de plataforma. Mapear: cobro recurrente OK -> Active, CurrentPeriodEndsAt += 1 mes; cobro fallido -> PastDue (dispara gracia de 1.2); cancelado / N fallos -> Canceled/Expired -> bloqueo.
3) Cambio de plan: upgrade -> ajusta monto del preapproval (o cancela+recrea según permita la API vigente), inmediato. Downgrade -> al fin del periodo.
4) Cancelación: el owner cancela -> cancela el preapproval en MP, activo hasta CurrentPeriodEndsAt, luego bloqueo.
5) Ofrece periodicidad trimestral/anual con descuento además de mensual (estructura; el precio lo afina el negocio) para bajar churn por tarjetas que fallan.
Devuelve el estado por API para el muro de pago. La UI de selección de plan + el brick de tarjeta de MP es Angular aparte (FE-4).

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores.
• Con credenciales SANDBOX de MP: crear preapproval deja la cuenta Active; simular webhook de cobro fallido la pasa a PastDue.
• DETENTE. Cierra Fase 1 backend. Resume y espera el OK.

```

FASE 1 — Frontend (web admin de la emprendedora)
Sobre la app Angular existente en `C:\Codigos\sellgeneral`. Web orientada a app, solo para el admin de la emprendedora. (La app nativa de clienta es Fase 2.)
FE-0 — Kit de marca por tenant (backend, prerequisito del tema)
Única pieza de backend de esta sección: sin estos campos, el panel no tiene de dónde leer la marca.

```
FASE 1 · FRONTEND — FE-0 (backend): Kit de marca por tenant. Habilita que el panel (y luego la app) tomen la marca de cada negocio: color + logo + banner + nombre. Sobre 0.1–0.5 y 1.0–1.2 ya aplicados.

CONTEXTO: Business ya existe con Name, Slug, City, FrontendUrl, PlanTier, SubscriptionStatus, TrialEndsAt, CurrentPeriodEndsAt, MercadoPagoAccessToken (encriptado). CloudinaryService sube a "{business.Slug}/{folder}" (0.5). Policies de rol por negocio activo (0.2). Endpoint de "estado de cuenta" (1.2). Motor IEntitlementService (1.0).

1) Agrega a Business estos campos de marca (el "nombre" YA es Name; no lo dupliques):
   - LogoUrl (string, null)
   - BannerUrl (string, null)
   - BrandPrimaryColor (string, hex "#RRGGBB", NOT NULL, default "#6C4AE0")
   - BrandAccentColor (string, hex, null)
   Migración EF. NO gatees la marca por plan: la identidad de la tienda está disponible en TODOS los planes (Entrada incluida).
2) En el seeder de DEV, el Business #1 (Regi Bazar) lleva su marca REAL: BrandPrimaryColor="#FF0072" (su rosa, el del canal FCM del inventario), BrandAccentColor=null, LogoUrl/BannerUrl=null por ahora.
3) Endpoints de subida (solo Owner/Admin del negocio ACTIVO, policies de 0.2; valida X-Business-Id):
   - POST /api/business/brand/logo    (multipart, imagen)
   - POST /api/business/brand/banner  (multipart, imagen)
   Cada uno: valida tipo (png/jpg/webp) y tamaño (logo ≤2MB, banner ≤5MB); sube vía CloudinaryService a "{slug}/brand"; guarda la SecureUrl en LogoUrl/BannerUrl del Business activo; devuelve la URL. Reusa el patrón de subida de DriverController. Nada de disco local.
4) Endpoint para editar el resto del kit (solo Owner/Admin):
   - PUT /api/business/brand   body { name?, brandPrimaryColor?, brandAccentColor? }
   Valida colores con regex hex "#RRGGBB". Actualiza SOLO el Business activo.
5) Endpoint de bootstrap que el FRONTEND lee al cargar: EXTIENDE el "estado de cuenta" de 1.2 (o crea GET /api/business/me) para devolver en UNA llamada marca + estado de plan + features:
   { id, name, slug, city,
     brand: { logoUrl, bannerUrl, brandPrimaryColor, brandAccentColor },
     subscription: { effectivePlan, subscriptionStatus, trialEndsAt, currentPeriodEndsAt, isLocked, daysLeft },
     features: [ ...Feature habilitadas del plan efectivo (IEntitlementService) ] }
   Scope: negocio activo (X-Business-Id validado contra Membership).
6) Tests: subir logo guarda la URL en el Business correcto y NO en otro tenant; PUT rechaza color inválido; GET /me trae SU marca y SUS features; un Account sin Membership Owner/Admin en el negocio activo recibe 403 al editar marca.
No toques el frontend en este paso.

── ALTO Y VALIDA ──
• `dotnet build` → 0 errores. Migración en DEV. Tests del punto 6 en verde.
• DETENTE. Resume y espera el OK.

```

FE-1 — Registro / Acceso
Adapta el login al Account nuevo: teléfono (OTP) + Facebook, guarda memberships y negocio activo, envía X-Business-Id.

```
FASE 1 · FRONTEND — FE-1: Pantalla de Registro/Acceso sobre el Account nuevo. Sobre la app Angular existente en `C:\Codigos\sellgeneral` (v21, standalone + Tailwind 4). El backend de 0.2 ya expone login por Email/Password, Teléfono (OTP) y Facebook; el JWT trae sub=AccountId y las Memberships.

CONTEXTO DEL CÓDIGO EXISTENTE (no inventes rutas):
- Login actual: src/app/features/auth/login
- Servicios: src/app/core/services/auth.service.ts (hoy guarda en localStorage rb_token, rb_name, rb_role, rb_expires), api.service.ts
- Interceptor: src/app/core/interceptors/auth.interceptor.ts (hoy inyecta Bearer en todas las peticiones)
- Guard: src/app/core/guards/auth.guard.ts

1) Rehaz AuthService para el modelo Account:
   - Guarda: token (rb_token), accountId, displayName, y la lista de Memberships [{businessId, businessName, role}]. Reemplaza el rb_role plano por las memberships.
   - "Negocio activo": guarda activeBusinessId. Si el Account tiene 1 sola Membership, selecciónalo solo. Si tiene varias, deja un selector (se usa en FE-5).
   - Expón isAuthenticated, currentMemberships, activeBusinessId (signal u observable).
2) Actualiza auth.interceptor.ts: además del Bearer, agrega el header X-Business-Id con el activeBusinessId en las peticiones a /api/** de admin. Las vistas públicas por token NO lo necesitan.
3) Pantalla de acceso (rediseña features/auth/login con estética app-first, coherente con Tailwind 4):
   - Opción A: teléfono → pide número → backend manda OTP → captura el código de 6 dígitos → valida. (Si el proveedor SMS aún no está, deja el flujo conectado al endpoint y un modo DEV que acepte un código fijo, claramente marcado // DEV.)
   - Opción B: "Entra con Facebook" (Facebook Login, public_profile) → manda el token al backend → recibe sesión.
   - Email/Password queda como acceso legacy (link discreto "Acceso de equipo").
4) Tras autenticar: si el Account no tiene ninguna Membership como Owner → manda a FE-2 (alta de negocio). Si ya es Owner/Admin/Driver → manda al panel.
5) Maneja 401 (error.interceptor.ts) limpiando sesión y volviendo a esta pantalla.

── ALTO Y VALIDA ──
• `ng build` → 0 errores TS.
• `ng serve`: la pantalla carga; el flujo de teléfono (modo DEV) deja entrar; sin errores en consola.
• DETENTE. Di la ruta exacta para probar y qué esperar. Espera el OK.

```

FE-2 — Alta de negocio (onboarding) + marca inicial
Crea tu tienda → te vuelves Owner → arranca la prueba Pro → capturas tu kit de marca.

```
FASE 1 · FRONTEND — FE-2: Alta de negocio self-serve + captura de marca inicial. Sobre la app Angular existente. Usa el endpoint de onboarding de 1.2 (crea Business, vuelve Owner al Account, arranca prueba Pro 14d) y los endpoints de marca de FE-0.

1) Wizard de varios pasos (app-first), solo visible para un Account sin negocio propio:
   Paso 1 — Datos del negocio: nombre (Business.Name), ciudad. (Depot/coords pueden quedar default y ajustarse luego.)
   Paso 2 — Marca: color primario (color picker, default #6C4AE0), logo (POST /api/business/brand/logo), banner/portada (POST /api/business/brand/banner). Vista previa EN VIVO de su panel mientras elige (reusa el patrón del mockup aprobado: barra superior con su color + logo + nombre).
   Paso 3 — Listo: confirma que arrancó su prueba Pro de 14 días (lee subscription.trialEndsAt de GET /api/business/me) y entra al panel.
2) Al crear el negocio: setea activeBusinessId en AuthService y refresca memberships.
3) Copy desde el lado de la emprendedora, español, sentence case. Logo y banner son OPCIONALES (puede completarlos en FE-5); no bloquees el alta por no subirlos.
4) Sin pedir tarjeta aquí (la tarjeta es el día 14, FE-4).

── ALTO Y VALIDA ──
• `ng build` → 0 errores.
• `ng serve`: crear un negocio de prueba funciona end-to-end (queda Owner, ve su marca en preview, entra al panel). Sin errores en consola.
• DETENTE. Ruta de prueba + qué validar. Espera el OK.

```

FE-3 — Planes + estado de suscripción + muro
Toma como referencia visual el mockup ya aprobado (mockup-fase1.jsx).

```
FASE 1 · FRONTEND — FE-3: Pantalla de Planes + estado de suscripción + muro de bloqueo. Sobre la app Angular. Referencia visual: el mockup aprobado (mockup-fase1.jsx): tres planes Entrada/Pro/Elite + Enterprise, banner de prueba, muro de pago.

CONTEXTO: GET /api/business/me (FE-0/1.2) devuelve subscription { effectivePlan, subscriptionStatus, trialEndsAt, currentPeriodEndsAt, isLocked, daysLeft } y features[]. El motor da 402 en endpoints bloqueados.

1) Pantalla de planes (precios configurables que vienen del backend o de una fuente compartida, NO hardcodees features sueltas): Entrada $129, Pro $250 (destacado "Lo que más eligen"), Elite $460 MXN/mes; Enterprise = "Contáctanos". Listas de features EXACTAS al corte definido (base / +Pro / +Elite).
2) Banner de estado (lee subscription):
   - Trialing → "Estás probando Pro — te quedan {daysLeft} días · sin tarjeta todavía".
   - PastDue → aviso de cobro fallido + días de gracia.
   - Active → plan actual y próximo cobro (currentPeriodEndsAt).
3) Muro de bloqueo (isLocked === true): route-guard/overlay que, con plan efectivo bloqueado, lleva a planes con copy "Tu prueba terminó. Elige un plan para seguir." y NO deja usar el panel. (Bloquea al Owner/Admin; las vistas públicas por token no viven en esta app de admin.)
4) Interceptor de 402: cuando CUALQUIER endpoint responde 402 {feature, requiredPlan}, muestra un modal de upsell ("Esta función es del plan {requiredPlan}") con botón a planes. Centraliza en error.interceptor.ts.
5) Botón "Elegir/Activar {plan}" → navega a FE-4 (pago) con el plan elegido.

── ALTO Y VALIDA ──
• `ng build` → 0 errores.
• `ng serve`: la pantalla refleja el estado real de /me; simular isLocked=true muestra el muro; un 402 de prueba dispara el modal de upsell.
• DETENTE. Ruta + qué validar. Espera el OK.

```

FE-4 — Pago (conversión día 14) con brick de Mercado Pago
@mercadopago/sdk-js ya está instalado. Credenciales de PLATAFORMA.

```
FASE 1 · FRONTEND — FE-4: Pantalla de pago (conversión día 14) con el brick de Mercado Pago. Sobre la app Angular. La dependencia @mercadopago/sdk-js YA está instalada (inventario). Usa los endpoints de suscripción de 1.3 (preapproval con credenciales de PLATAFORMA).

1) Resumen del plan elegido (de FE-3): nombre, precio, y selector de periodicidad Mensual / Trimestral (-10%) / Anual (-20%) — los montos/descuentos los entrega el backend (1.3); no los inventes en el front.
2) Brick de tarjeta de Mercado Pago con @mercadopago/sdk-js (el componente vigente de Suscripciones / Card Brick). NO almacenes datos de tarjeta; todo lo maneja MP. Usa la PublicKey de PLATAFORMA expuesta por el backend, NUNCA la del tenant.
3) Al confirmar: llama al endpoint de 1.3 que crea el preapproval. En éxito → el backend deja SubscriptionStatus=Active; refresca GET /api/business/me y entra al panel ya desbloqueado. Errores de MP (tarjeta rechazada, etc.) con mensajes claros desde el lado del usuario, sin tono de disculpa.
4) Copy: "Se te cobra hoy. Tu tarjeta nunca se guarda con nosotros." Deja claro que se renueva solo y se puede cancelar.
5) Sección simple "Administrar suscripción": enlaza a los endpoints de 1.3 (upgrade inmediato, downgrade a fin de periodo, cancelar).

── ALTO Y VALIDA ──
• `ng build` → 0 errores.
• `ng serve` con credenciales SANDBOX de MP: el brick carga; un pago de prueba deja la cuenta Active y desbloquea el panel. Sin errores en consola.
• DETENTE. Ruta + qué validar (incluye qué credenciales sandbox usar). Espera el OK.

```

FE-5 — Panel temado + editor de marca + gating por plan
Amarra todo: el panel real toma la marca del negocio en vivo, se edita, y muestra/oculta/bloquea features por plan.

```
FASE 1 · FRONTEND — FE-5: El panel de admin existente, ahora (a) temado con la marca del negocio en vivo, (b) con pantalla para editar su marca, y (c) con las features mostradas/ocultas/bloqueadas según el plan. Sobre la app Angular existente (features/admin/*).

CONTEXTO: GET /api/business/me da brand { logoUrl, bannerUrl, brandPrimaryColor, brandAccentColor }, name, y features[]. Edición de marca en FE-0 (PUT /api/business/brand, POST .../logo, .../banner). El admin actual vive en src/app/features/admin (layout, dashboard, orders, clients, routes, financials, reports, pos, tandas, raffles, cami, etc.).

1) TEMATIZACIÓN EN VIVO: al cargar el panel, lee /me y aplica la marca como CSS custom properties en el root del layout admin (ej. --brand-primary, --brand-accent, y deriva tints). Refactoriza los colores fijos del layout/admin para que usen esas variables (en vez de rosa hardcodeado). El logo y el nombre del negocio aparecen en la barra superior; el banner donde aplique (encabezado del dashboard). Resultado: el MISMO panel se ve con la marca de cada tenant, como en el mockup aprobado. Cuida la especificidad de CSS (Tailwind 4 + variables).
2) PANTALLA "Mi marca" (nueva, dentro de admin, solo Owner/Admin): editar nombre, color primario (picker), color de acento, subir/cambiar logo y banner. Vista previa en vivo. Guarda con los endpoints de FE-0. Al guardar, re-aplica las variables sin recargar.
3) GATING POR PLAN (usa features[] de /me; refuerza lo que el backend ya bloquea con 402):
   - Oculta o marca con candado las entradas de menú/acciones cuyas features NO estén en el plan: Financials, TandasRaffles, Pos, FacebookImport, LivePush, LiveGpsTracking → Pro; CamiAssistant, TrafficRouteOptimization, Exports → Elite. Las base (captura, clientas+tags, link, estatus, cuenta de clienta, lealtad) siempre visibles.
   - Al tocar una función bloqueada: modal de upsell (el de FE-3) con el plan requerido. NUNCA dependas SOLO de ocultar en UI: el backend ya devuelve 402; esto es la capa visual.
   - Límite de repartidores (MaxDrivers): en rutas/repartidores, si el plan es Entrada (1), bloquea agregar el 2º con upsell.
4) Switch de contexto (Owner que también es clienta de otros negocios) queda ESBOZADO, no es foco de Fase 1: si el Account tiene varias Memberships, deja el selector de negocio activo en la barra; la cara de "compradora" es Fase 2. Solo asegúrate de que cambiar de negocio activo re-tema el panel.

── ALTO Y VALIDA ──
• `ng build` → 0 errores.
• `ng serve`: el panel toma color+logo+nombre del negocio; cambiar la marca en "Mi marca" lo re-tema sin recargar; una cuenta en plan Entrada ve con candado las funciones Pro/Elite y aparece el upsell; agregar 2º repartidor en Entrada se bloquea.
• DETENTE. Con el OK de Eduardo, Fase 1 frontend queda cerrada.

```

MIGRACIÓN — Regi Bazar → Tenant #1
Se ejecuta hasta el CORTE (Etapas 5–6 de tu checklist), después de probar el sistema con una vendedora beta. Corre sobre datos IRREEMPLAZABLES: el `--verify` es obligatorio, primero contra una COPIA (branch de Neon), y nunca contra producción hasta que todo lo demás esté verde.
M.1 — Migrador (proyecto de consola, una sola vez)

```
Construye una herramienta de migración de UNA sola vez: un proyecto de consola .NET nuevo en la solución (EntregasApi.Migrator) que copie el negocio real de RegiBazar desde la base VIEJA (single-tenant) a la base NUEVA multi-tenant, dejándolo como Tenant #1. Corre sobre datos IRREEMPLAZABLES: priorízala correcta y reversible sobre todo lo demás.

CONTEXTO
- ORIGEN: base vieja "neondb" (PostgreSQL 17, Neon), esquema single-tenant SIN BusinessId. 37 tablas, ~6,030 filas de negocio. Nomenclatura MIXTA confirmada: 28 tablas PascalCase con columnas PascalCase ("Orders"."AccessToken") y 8 tablas snake_case con columnas snake_case (tandas.access_token).
- DESTINO: base NUEVA (otro proyecto Neon), esquema multi-tenant YA creado con `dotnet ef database update` (prompts 0.1-0.2 aplicados: existen Account, Business, Membership y la columna BusinessId; la tabla User YA NO existe). El seeder de DEV NO debió correr aquí (destino vacío).
- Dos connection strings separados. El de ORIGEN se abre en SESIÓN READ ONLY. PROHIBIDO escribir una sola fila en origen.

PRINCIPIOS NO NEGOCIABLES
1. PRESERVAR IDs Y TOKENS IDÉNTICOS. Cada PK (int o Guid) y cada token se copia EXACTO, sin regenerar. NO normalices nombres de tabla/columna, NO cambies tipos, NO "mejores" el esquema. Copia literal. (Las URLs de Cloudinary llevan el Order.Id adentro —evidence_970…— y los links de Messenger llevan el AccessToken: cambiar uno rompe fotos o links vivos.)
2. UNA TRANSACCIÓN en destino: todo o nada. Si algo falla, ROLLBACK y el destino queda limpio.
3. ANTES de migrar, verifica que el destino esté VACÍO (sin Business Id=1). Si ya existe, ABORTA (evita doble corrida).
4. Orden de inserción padres→hijos (integridad de origen perfecta, 0 huérfanos): Business primero; luego Account, luego Membership; luego raíces e hijas según el grafo de FKs. Si hay FK circular, SET CONSTRAINTS ... DEFERRED dentro de la transacción.

TRANSFORMACIONES (lo que NO es copia 1:1)
A. Crear Business Id=1 (no existe en origen): Name="Regi Bazar", Slug="regibazar", City="Nuevo Laredo", DepotLat=27.4861, DepotLng=-99.5069, GeocodingRegion="Nuevo Laredo, Tamaulipas, MX", GeminiBusinessName="Regi Bazar", BrandPrimaryColor="#FF0072", PlanTier="Elite", SubscriptionStatus=Active, TrialEndsAt=null, CurrentPeriodEndsAt=null, IsActive=true.
   MercadoPagoAccessToken NO está en la base de origen (vive en appsettings.json). Pásalo como parámetro/secreto (--rb-mp-token) y guárdalo ENCRIPTADO. Si no se pasa, déjalo null y registra advertencia.
B. Users → Accounts + Memberships (User ya no existe en destino): por cada fila de Users crea un Account (Email y PasswordHash copiados; DisplayName = nombre del user o derivado del email; Phone/FacebookUserId = null) y un Membership(AccountId, BusinessId=1, Role) mapeando Rol: Admin→Owner, Driver→Driver, Scaner→Scaner (origen: 1 Admin, 1 Driver, 2 Scaner). MANTÉN en memoria un diccionario User.Id(origen) → Account.Id(destino).
C. Remapear FKs que apuntaban a User: CashRegisterSession.UserId ahora apunta a Account.Id; tradúcelo con el diccionario de (B). Es 1 sola fila, hazlo bien.
D. Estampar BusinessId=1 en TODA fila de TODA tabla que tenga la columna BusinessId (raíces e hijas denormalizadas).
E. Client.AccountId = null en todas (nadie ha reclamado su perfil aún).
F. AppSettings: copia la fila única (Id=1, DefaultShippingCost=60, LinkExpirationHours=72) y estámpale BusinessId=1.

COLISIONES DE NOMBRE (trampa crítica) — trata estas 4 como entidades SEPARADAS:
- products (minúscula) = TandaProduct (4 filas)  ≠  "Products" (POS, 0 filas)
- payments (minúscula) = TandaPayment (255 filas) ≠  "OrderPayments" (651 filas)
En Postgres products ≠ "Products". Entrecomilla por tabla. SELECT ... FROM products lee TANDAS, no el POS.

FOTOS: 3 filas de DeliveryEvidences (pedidos 118, 168, 190) tienen ImagePath LOCAL ("evidence/...") que el sistema nuevo no sirve. La herramienta NO sube fotos por su cuenta: acepta un parámetro opcional de mapeo {EvidenceId → URL Cloudinary} (de un rescate previo); si se provee, escribe esa URL; si no, copia la ruta tal cual y registra ADVERTENCIA por cada una. DriverExpenses.EvidencePath está 100% vacío: nada ahí.

SECUENCIAS (gotcha Postgres): tras insertar PKs int explícitos, RESETEA cada secuencia int-PK: setval(pg_get_serial_sequence('"Tabla"','Id'), MAX("Id")), sino el próximo insert de la app colisiona. Si alguna columna es GENERATED ALWAYS AS IDENTITY, inserta con OVERRIDING SYSTEM VALUE. Las PKs Guid no llevan secuencia.

TABLAS VACÍAS: "Products"(POS), SalesPeriods, ClientMergeAudits, LiveProducts, LiveSpokenOrders, LiveCommentOrders, LiveCandidates = 0 filas; cópialas vacías. LiveSessions (13) se migra: no tiene hijos.

MODO --verify (OBLIGATORIO, es la PRUEBA de fidelidad). Sin escribir nada, compara ORIGEN vs DESTINO y reporta PASS/FAIL por punto:
1. Conteo de filas por tabla: destino == origen. Más: Business=1, Account=4, Membership=4 con distribución 1 Owner/1 Driver/2 Scaner.
2. Tokens en destino: Orders.AccessToken(669), DeliveryRoutes.DriverToken(41), tandas.access_token(6), OrderPackages.QrCodeValue(7), Clients.Name(319) → 0 nulos, 0 duplicados, y conjunto de valores IDÉNTICO al de origen.
3. Spot-check de IDs: existen en destino los Order.Id 118, 168, 190 y 970 con su MISMO AccessToken y BusinessId=1.
4. Secuencias: cada secuencia int-PK > MAX(id) de su tabla.
5. Integridad referencial en destino: re-corre los 9 chequeos de huérfanos → 0.
6. Identidad: CashRegisterSession.UserId apunta a un Account válido.
Imprime un veredicto PASS/FAIL global.

ENTREGABLES: el proyecto EntregasApi.Migrator (modo copia + --verify), un README corto (los dos connection strings, --rb-mp-token, el mapeo de fotos). NADA que escriba en la base de ORIGEN. No toques el resto de la solución.

── ALTO Y VALIDA ──
• `dotnet build` del proyecto Migrator → 0 errores.
• NO corras la copia real. Primero `--verify` y la copia se ensayan contra un BRANCH de Neon (copia), nunca producción.
• DETENTE. Resume cómo se invoca (copia y --verify) y recuerda a Eduardo que el ensayo va contra copia, y el corte real sigue su checklist de Etapas 5–6.

```

Notas finales

* Runbook de corte: los pasos operativos del corte (ventana sin live, branch fresco, ensayo sobre copia, apuntar DNS, rollback) ya los tienes como checklist. Este kit cubre la construcción; el migrador (M.1) es la última pieza de código y su ejecución sigue tu checklist.
* Lo que queda fuera, a propósito: la migración de Maps a OSM/MapLibre (no bloquea nada; tu app corre con Google hoy) y toda la Fase 2 (app nativa de clienta en Flutter, switch de contexto compradora). Se especifican cuando los pidas.
* Recordatorio legal (no de código): antes de cobrar a terceras, separar la cuenta de plataforma de Mercado Pago de la personal de tu esposa, y definir quién es el dueño legal de la plataforma.