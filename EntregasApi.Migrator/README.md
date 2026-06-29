# EntregasApi.Migrator

Migrador de **una sola ejecucion** que copia el negocio real de Regi Bazar desde la base VIEJA
(single-tenant) a la base NUEVA (multi-tenant, Neni's App), dejandolo como Tenant #1.

Se ejecuta al **corte** (Etapas 5-6 de tu checklist de migracion). Antes del corte se ensaya
contra un **branch de Neon** (copia), nunca contra produccion.

## Prerrequisitos

- .NET 8 SDK (mismo que el resto de la solucion).
- Base de ORIGEN (Regi Bazar single-tenant) accesible en modo lectura.
- Base de DESTINO (Neni's App multi-tenant) con las migraciones de EF aplicadas (`dotnet ef database update`).
  - El seeder de DEV **NO** debe haber corrido alli (el migrador aborta si ya hay Business Id=1).
- Acceso a los archivos de DataProtection de la app para encriptar `--rb-mp-token` con el mismo
  protector (mismas llaves, misma `ApplicationName="EntregasApi"`). Por default .NET los guarda en
  `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`. **El migrador debe correr como el mismo usuario
  que la app**, o tener acceso a esa carpeta.

## Compilar

```bash
dotnet build EntregasApi.Migrator/EntregasApi.Migrator.csproj
```

Resultado esperado: 0 errores, 0 warnings.

## Modos

### `--verify` (PRIMERO, siempre)

NO escribe nada. Compara origen vs destino en 6 chequeos:

1. Conteo de filas por tabla + distribucion de roles en Memberships (1 Owner / 1 Driver / 2 Scaner).
2. Tokens: `Orders.AccessToken` (669), `DeliveryRoutes.DriverToken` (41), `tandas.access_token` (6),
   `OrderPackages.QrCodeValue` (7), `Clients.Name` (319) -> 0 nulos, 0 duplicados, conjunto
   IDENTICO origen vs destino.
3. Spot-check: Orders 118, 168, 190, 970 existen en destino con su MISMO AccessToken y BusinessId=1.
4. Secuencias: cada secuencia int-PK esta alineada con `MAX(Id)` de su tabla.
5. Integridad referencial: 15 chequeos de huerfanos -> 0.
6. Identidad: `CashRegisterSessions.AccountId` apunta a un Account valido.

Imprime un veredicto `PASS` / `FAIL` global.

```bash
dotnet run --project EntregasApi.Migrator -- \
    --verify \
    --source "Host=...neondb...;Database=neondb" \
    --dest   "Host=...neondb...;Database=sellgeneral"
```

### Copia (una sola vez, en el corte)

Realiza todo lo de `--verify` y ademas escribe en destino. Una sola transaccion: si algo falla,
ROLLBACK y el destino queda limpio.

```bash
dotnet run --project EntregasApi.Migrator -- \
    --source "Host=...neondb...;Database=neondb" \
    --dest   "Host=...neondb...;Database=sellgeneral" \
    --rb-mp-token "APP_USR-..." \
    --evidence-map "./evidence-map.json"
```

| Parametro         | Obligatorio | Descripcion                                                                                           |
|-------------------|-------------|-------------------------------------------------------------------------------------------------------|
| `--source`        | si          | Connection string del origen (Regi Bazar single-tenant). Se abre en sesion READ ONLY.                 |
| `--dest`          | si          | Connection string del destino (Neni's App multi-tenant). El migrador aborta si ya hay Business Id=1.  |
| `--verify`        | no          | Si se pasa, NO escribe; solo ejecuta los 6 chequeos.                                                  |
| `--rb-mp-token`   | recomendado | Access token de MP de la vendedora. Se encripta con DataProtection. Si se omite, queda NULL + warning. |
| `--evidence-map`  | no          | JSON `{ "evidenceId": "urlCloudinary" }` para reescribir las 3 evidencias legacy con ruta local.      |
| `--verbose`       | no          | Log a nivel Debug.                                                                                    |

## Transformaciones que aplica (segun ROLYCONTEXTO M.1)

- **A.** Crea `Business Id=1` ("Regi Bazar", `Slug="regibazar"`, depot 27.4861/-99.5069,
  `BrandPrimaryColor="#FF0072"`, `PlanTier="Elite"`, `SubscriptionStatus=Active`).
  `MercadoPagoAccessToken` se encripta con el mismo protector de la app si se pasa `--rb-mp-token`.
- **B.** `Users` (origen) -> `Accounts` + `Memberships` (destino). Mapeo de roles: `Admin`->`Owner`,
  `Driver`->`Driver`, `Scaner`->`Scaner`. Conserva PKs (`Account.Id = User.Id`).
- **C.** `CashRegisterSessions.UserId` -> `AccountId` (identidad: ambos son el mismo int porque
  `Account.Id = User.Id`). Si algun `UserId` no existe en el diccionario (huérfano), se pone NULL
  y se registra WARN.
- **D.** Estampa `BusinessId=1` en TODA fila de TODA tabla con esa columna.
- **E.** `Clients.AccountId = NULL` en todas (nadie ha reclamado perfil).
- **F.** `AppSettings`: copia la fila unica de origen y la estampa `BusinessId=1`.

## Reglas duras

- **PRESERVA IDS Y TOKENS IDENTICOS**: cada PK (int o Guid) y cada token se copia EXACTO.
  NO normaliza nombres de tabla/columna, NO cambia tipos, NO "mejora" el esquema.
- **UNA TRANSACCION** en destino: todo o nada.
- **PRE-CHEQUEO**: si el destino ya tiene `Business Id=1` o memberships/orders/clients, aborta
  para evitar doble corrida.
- **CONEXION READ ONLY en origen**: la sesion ejecuta `SET default_transaction_read_only = on`.
  Cualquier intento de escritura fallara con error de Postgres (defensa en profundidad).
- **SECUENCIAS**: tras insertar PKs int explicitos, `setval` cada secuencia int-PK al `MAX(Id)`.
  Las tablas Guid-PK no llevan secuencia.
- **COLISIONES DE NOMBRE**: el plan exige tratar `products`/`Products`, `payments`/`OrderPayments`,
  `tandas`/`Orders` como entidades SEPARADAS. El migrador respeta el case original del esquema
  fuente y los doble-comilla al construir SQL.

## Fotos legacy

3 filas de `DeliveryEvidences` (pedidos 118, 168, 190) tienen `ImagePath` local
(`evidence/...`). El sistema nuevo no sirve esas rutas. Pasale un JSON con las URLs de Cloudinary:

```json
{
  "118": "https://res.cloudinary.com/xxx/image/upload/v123/regibazar/evidence/118_xxx.jpg",
  "168": "https://res.cloudinary.com/xxx/image/upload/v123/regibazar/evidence/168_xxx.jpg",
  "190": "https://res.cloudinary.com/xxx/image/upload/v123/regibazar/evidence/190_xxx.jpg"
}
```

Si una entrada falta, se conserva la ruta local y se registra WARN (no aborta).

## Orden de invocacion (en el corte)

1. **Ensayo en copia**: crear un branch de Neon de la base de PROD (single-tenant). Apuntar una
   base Neon NUEVA como destino (multi-tenant, migrada con `dotnet ef database update`, sin seeder
   DEV). Correr `--verify` -> debe dar PASS. Luego correr la copia completa. Repetir el `--verify`
   con el mismo origen y destino para confirmar que la copia dejo todo OK.
2. **Corte real** (ventana sin live):
   - Apuntar la base de PROD vieja como `--source` y la base nueva de PROD como `--dest`.
   - Correr la copia completa. Si la transaccion falla, el destino queda limpio.
   - Actualizar el connection string de la app a la nueva base.
   - Correr `--verify` una vez mas para cerrar el check.
3. **Rollback**: si algo sale mal despues de la copia, volver la app a la base vieja
   (mientras siga accesible en modo read-only) es el camino mas seguro.

## No hace (a proposito)

- NO sube fotos a Cloudinary. Solo reescribe `ImagePath` con URLs ya existentes en el JSON.
- NO migra a la cuenta de MP de plataforma. Eso se hace via el flujo de conversion dia 14
  (FE-4) o via soporte manual.
- NO crea el segundo tenant demo. Solo el Tenant #1 (Regi Bazar).

## Tests

```bash
dotnet test EntregasApi.Migrator.Tests/EntregasApi.Migrator.Tests.csproj
```

Cubre el parser de CLI (10 casos) y la estructura del plan de migracion (8 casos).
Total: 18/18 PASS.
