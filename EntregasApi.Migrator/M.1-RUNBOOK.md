# M.1 — Cómo correr la migración de Regi Bazar (one-shot)

> Documento operativo — no toca código. Acompaña al `EntregasApi.Migrator` ya construido.
> Léelo completo antes de correr. Cada paso es verificable.

## Resumen

La migración es **una sola corrida** de la herramienta `EntregasApi.Migrator`. No hay
"ventana sin live" ni "switch de DNS" ni "rollback de 5 minutos": cuando todo está listo
se corre el migrador, se valida, y se actualiza el connection string de la app a la
base nueva. Eso es todo.

Si algo sale mal después, se revierte el cambio en el connection string (operación
normal de deploy) y la base vieja sigue accesible para investigar.

---

## Paso 1 — Crear la base de destino

Vas a necesitar **2 branches de Neon** (uno para ensayo, otro para producción nueva)
y correr las migraciones de EF en cada uno.

### 1.1. Crear los branches en Neon

En el panel de Neon (https://console.neon.tech), sobre tu proyecto actual:

1. En la barra lateral, click en **Branches**.
2. Click **Create Branch**.
3. Configurar:
   - **Name:** `staging-cutover`
   - **Parent branch:** `main` (la rama donde vive `neondb`, la base actual de Regi Bazar)
4. Click **Create Branch**.
5. Repetir para crear **`prod-cutover`** (mismo parent: `main`).

Cada branch tiene su propio connection string. Para obtenerlo:

1. Click sobre el branch en la lista.
2. Click **Connection Details**.
3. Elegir **Pooled connection** (recomendado para la app; usa el puerto pooled).
4. Copiar el connection string. Luce así:
   ```
   Host=ep-xxx-pooler.c-yyy.us-east-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=xxx;SSL Mode=Require;Trust Server Certificate=true
   ```

Vas a tener 3 connection strings en total:

| Nombre local | Branch Neon | Propósito |
|---|---|---|
| `ORIGEN` | `main` (`neondb`) | base actual de Regi Bazar — solo lectura |
| `DESTINO_ENSAYO` | `staging-cutover` | ensayo del migrador |
| `DESTINO_PROD` | `prod-cutover` | producción nueva, donde correrá la app |

### 1.2. Correr las migraciones de EF en cada destino

Las migraciones crean el esquema multi-tenant (Accounts, Businesses, Memberships, etc.)
sin datos. **No** corren el seeder de DEV (eso lo prohíbe el plan: el seeder chocaría
con el migrador).

#### En Windows CMD

```cmd
set ConnectionStrings__Default="<DESTINO_ENSAYO>"
dotnet ef database update --project "C:\Codigos\sellgeneral-api\EntregasApi.csproj"
set ConnectionStrings__Default=

set ConnectionStrings__Default="<DESTINO_PROD>"
dotnet ef database update --project "C:\Codigos\sellgeneral-api\EntregasApi.csproj"
set ConnectionStrings__Default=
```

#### En PowerShell

```powershell
$env:ConnectionStrings__Default = "<DESTINO_ENSAYO>"
dotnet ef database update --project "C:\Codigos\sellgeneral-api\EntregasApi.csproj"
Remove-Item Env:ConnectionStrings__Default

$env:ConnectionStrings__Default = "<DESTINO_PROD>"
dotnet ef database update --project "C:\Codigos\sellgeneral-api\EntregasApi.csproj"
Remove-Item Env:ConnectionStrings__Default
```

#### En Git Bash (o WSL)

```bash
ConnectionStrings__Default="<DESTINO_ENSAYO>" dotnet ef database update --project "/c/Codigos/sellgeneral-api/EntregasApi.csproj"

ConnectionStrings__Default="<DESTINO_PROD>" dotnet ef database update --project "/c/Codigos/sellgeneral-api/EntregasApi.csproj"
```

> El doble guion bajo `__` es la convención de .NET para reemplazar `:` en keys
> anidadas (`ConnectionStrings:Default` → `ConnectionStrings__Default`).

#### Si no tienes `dotnet-ef` instalado

```bash
dotnet tool install --global dotnet-ef --version 8.0.11
```

### 1.3. Verificar que cada destino está vacío

Conectate a cada destino con `psql` o tu cliente favorito y corré:

```sql
SELECT COUNT(*) FROM "Businesses";   -- esperado: 0
SELECT COUNT(*) FROM "Accounts";     -- esperado: 0
SELECT COUNT(*) FROM "Memberships";  -- esperado: 0
SELECT COUNT(*) FROM "Orders";       -- esperado: 0
SELECT COUNT(*) FROM "Clients";      -- esperado: 0
```

Si alguna tabla no está vacía, NO sigas. Probablemente alguien corrió el seeder de DEV
en ese destino (lo cual el plan prohíbe). Crea otro branch y vuelve a 1.1.

---

## Paso 2 — Verificación sin escribir (sobre el ensayo)

```bash
dotnet run --project C:\Codigos\sellgeneral-api\EntregasApi.Migrator -- ^
    --verify ^
    --source "<ORIGEN>" ^
    --dest   "<DESTINO_ENSAYO>"
```

**Esperado:**

```
====================== REPORTE DE VERIFICACION ======================
[PASS] 1. Conteo de filas
        OK en N tablas; roles OK (Owner=1/Driver=1/Scaner=2).
[PASS] 2. Tokens (no nulos, no dupes, conjunto identico)
        0 nulos, 0 duplicados y conjunto identico origen/destino en 5 conjuntos de tokens.
[PASS] 3. Spot-check Orders (118, 168, 190, 970)
        4/4 Orders OK con mismo AccessToken y BusinessId=1.
[PASS] 4. Secuencias int-PK
        Todas las secuencias int-PK estan alineadas con MAX(Id).
[PASS] 5. Integridad referencial (huerfanos)
        0 huerfanos en N chequeos.
[PASS] 6. Identidad (CashRegisterSession.AccountId)
        Todos los CashRegisterSession.AccountId apuntan a un Account valido.
======================================================================
Veredicto global: PASS
```

> **Si algo da FAIL: NO SIGAS.** Lo más probable es desincronización de origen
> (alguien insertó en `neondb` desde la última vez). Re-corre el ensayo cuando el origen
> esté estable.

---

## Paso 3 — Copia real (sobre el ensayo)

```bash
dotnet run --project C:\Codigos\sellgeneral-api\EntregasApi.Migrator -- ^
    --source "<ORIGEN>" ^
    --dest   "<DESTINO_ENSAYO>" ^
    --rb-mp-token "APP_USR-..."
```

`--evidence-map` no se usa (las 3 evidencias con ruta local eran datos de prueba).

**Esperado:** cada tabla reporta `-> N filas copiadas`. La transacción hace COMMIT al final.

Si en cualquier momento ves `crit: Migrador abortado con excepcion no controlada`, ve
directo a **Reversión** (§6).

---

## Paso 4 — Re-verificar (sobre el ensayo ya copiado)

Vuelve a correr el `--verify` con los mismos connection strings. Debe dar **PASS** otra
vez.

---

## Paso 5 — Smoke test manual (sobre el ensayo)

Apunta la app a `DESTINO_ENSAYO` (temporalmente, con env var o editando
`appsettings.Development.json`):

1. **Login** con el email del Owner y la contraseña actual.
2. **Abrir un pedido histórico** (Id 118 o 190) desde la lista.
3. **Abrir el link público** del pedido 118 con su `AccessToken` **sin autenticación**
   (`GET /api/pedido/{accessToken}`). Debe devolver 200.
4. **Abrir el link del repartidor** (`GET /api/driver/{driverToken}`) con un `DriverToken`
   real. Debe devolver 200.
5. **Listar 5 clientas** y verificar que los datos son los mismos.
6. **Listar 1 tanda y 1 sorteo** y verificar que `tandas` y `raffles` se copiaron bien
   y no se mezclaron con `Orders` o `payments`.

---

## Paso 6 — Promover a producción

Si los pasos 2–5 dieron PASS, repetí **3 y 4** apuntando a `DESTINO_PROD`:

```bash
dotnet run --project C:\Codigos\sellgeneral-api\EntregasApi.Migrator -- ^
    --verify ^
    --source "<ORIGEN>" ^
    --dest   "<DESTINO_PROD>"

dotnet run --project C:\Codigos\sellgeneral-api\EntregasApi.Migrator -- ^
    --source "<ORIGEN>" ^
    --dest   "<DESTINO_PROD>" ^
    --rb-mp-token "APP_USR-..."

dotnet run --project C:\Codigos\sellgeneral-api\EntregasApi.Migrator -- ^
    --verify ^
    --source "<ORIGEN>" ^
    --dest   "<DESTINO_PROD>"
```

---

## Paso 7 — Apuntar la app a la base nueva

En el host donde corre la app (servidor, contenedor, etc.):

1. Cambiá el `ConnectionStrings:Default` a `DESTINO_PROD` (env var o `appsettings.json`).
2. Reiniciá la app.
3. Repetí el smoke test del paso 5 contra producción.

Cuando esto termine, **ya está**. La app corre contra la base multi-tenant con los
datos de Regi Bazar migrados.

---

## Paso 8 — Reversión (si algo sale mal en cualquier momento)

No hay procedimiento especial. Revertí el connection string al `ORIGEN` y reiniciá
la app. La base vieja sigue accesible mientras no la borres. Investigá con calma
y reintentá cuando encuentres la causa.

**El migrador no se vuelve a correr.** Si necesitás reintentar la copia, primero
hay que limpiar el destino (`DROP SCHEMA public CASCADE; CREATE SCHEMA public;` +
`dotnet ef database update`).

---

## Apéndice A — Decisiones a tomar antes de migrar

| # | Decisión | Default recomendado |
|---|---|---|
| 1 | Cómo se cambia el connection string el día del cambio | env var en el host |
| 2 | ¿Cuánto tiempo de retención de la base vieja post-migración? | 30 días |

## Apéndice B — Comandos rápidos de psql

```sql
-- Conteos clave (origen)
SELECT 'Orders' AS tabla, COUNT(*) FROM "Orders"
UNION ALL SELECT 'Clients', COUNT(*) FROM "Clients"
UNION ALL SELECT 'Users', COUNT(*) FROM "Users"
UNION ALL SELECT 'AppSettings', COUNT(*) FROM "AppSettings";

-- Conteo destino post-copia (debe coincidir)
SELECT 'Businesses' AS tabla, COUNT(*) FROM "Businesses"
UNION ALL SELECT 'Accounts', COUNT(*) FROM "Accounts"
UNION ALL SELECT 'Memberships', COUNT(*) FROM "Memberships"
UNION ALL SELECT 'Orders', COUNT(*) FROM "Orders";

-- Conteo de roles (esperado Owner=1, Driver=1, Scaner=2)
SELECT "Role", COUNT(*) FROM "Memberships" GROUP BY "Role";

-- Spot-check de un pedido
SELECT "Id", "AccessToken", "BusinessId" FROM "Orders" WHERE "Id" IN (118, 168, 190, 970);

-- Verificar que un token publico sigue resolviendo
SELECT "Id", "Status" FROM "Orders" WHERE "AccessToken" = '<token-de-la-clienta>';
```

## Apéndice C — Lo que NO hace este runbook

- **No sube fotos a Cloudinary.** No hay 3 evidencias que rescatar; eran pruebas locales.
- **No migra a la cuenta de MP de plataforma.** Eso se hace vía el flujo de conversión
  día 14 (FE-4 del frontend) cuando Regi meta tarjeta a su propia suscripción.
- **No crea el segundo tenant demo.** Solo el Tenant #1 (Regi Bazar).
- **No toca datos de `__EFMigrationsHistory`.** El destino ya la trae poblada por
  `dotnet ef database update`.
