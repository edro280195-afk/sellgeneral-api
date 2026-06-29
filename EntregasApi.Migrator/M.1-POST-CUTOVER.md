# M.1 — Pasos restantes para cerrar la migracion

> **Estado actual:** la base `prod-cutover` (`ep-lively-wind-aty96jj0...c-9.us-east-1.aws.neon.tech/neondb`) tiene los datos de Regi Bazar migrados (Business=1, Accounts=4, Orders=743, etc.). El verificador dio **6/6 PASS** y la base original quedo **100% intacta** (16/16 snapshots coinciden). El smoke test parcial corrio y dio **4/4 PASS** en los endpoints publicos.
>
> **Lo que falta es operativo** — no hay mas codigo que escribir. Este archivo lista cada paso en orden, con tiempo estimado y la decision que tenes que tomar vos.

---

## Checklist general

```
[ ]  1. Verificar que la app este lista para reiniciar (5 min)
[ ]  2. Apuntar la app al nuevo destino y reiniciar (2-15 min)
[ ]  3. Correr el smoke test automatizado (2 min)
[ ]  4. Smoke test manual en el navegador (10 min)
[ ]  5. Verificar que la encriptacion de DataProtection funciona (5 min)
[ ]  6. Avisar a Regi (1 min)
[ ]  7. Monitoreo las primeras 24-72h (pasivo)
[ ]  8. Limpieza de archivos y seguridad (10 min)
[ ]  9. Despues de 30 dias: archivar la base vieja (10 min)
[ ] 10. Opcional: campana de captura de telefonos (no bloqueante)
```

---

## Paso 1 — Verificar que la app este lista para reiniciar (5 min)

**Que verificar antes de tocar nada:**

- [ ] Tenes el password real del Owner (`yazmin_vara@hotmail.com`) o lo podes recuperar
- [ ] El host donde corre la app tiene las **DataProtection keys de la misma maquina donde corrio el migrador** (ver Paso 5)
- [ ] No hay una captura en vivo de Regi en este momento (repartidor en ruta, live de TikTok, etc.)
- [ ] Tenes acceso al host de despliegue para reiniciar (IIS manager, docker, kubectl, ssh, etc.)

**Si algo de esto falla:** mejor esperar. No hay urgencia, la base vieja sigue funcionando.

---

## Paso 2 — Apuntar la app al nuevo destino y reiniciar (2-15 min)

### 2.1. Confirmar que `appsettings.json` esta actualizado

```powershell
# Esto debe mostrar el host NUEVO (c-9), no el viejo (c-4)
Select-String "Default" C:\Codigos\sellgeneral-api\appsettings.json
```

**Esperado:** `Host=ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech...` (ya esta hecho, esto es solo verificacion).

**Si muestra `ep-steep-bar-...c-4` (el viejo):** NO esta actualizado. Avisame antes de seguir.

### 2.2. Decidir como se pasa el password real a la app

El `appsettings.json` tiene `Password=dummy` (placeholder). El password real va por env var. **Tres opciones segun tu setup:**

**Opcion A — Env var en el host de la app (recomendado para prod):**
```bash
# Windows (cmd):
setx ConnectionStrings__Default "Host=ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_GmnX5gWr3QDc;SSL Mode=Require;Trust Server Certificate=true"
# Nota: setx requiere re-login para que tome efecto, o reiniciar la app.

# Windows (PowerShell, sesion actual):
$env:ConnectionStrings__Default = "Host=ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_GmnX5gWr3QDc;SSL Mode=Require;Trust Server Certificate=true"
# Nota: solo aplica a esta sesion. Para que persista, usa setx o configuralo en el IIS/app service.

# Linux/Docker env:
export ConnectionStrings__Default="Host=ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_GmnX5gWr3QDc;SSL Mode=Require;Trust Server Certificate=true"
```

**Opcion B — Editar `appsettings.Development.json`** (solo si la app corre en Development; este archivo SI esta en `.gitignore`):
```json
"ConnectionStrings": {
  "Default": "Host=ep-lively-wind-aty96jj0-pooler.c-9.us-east-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=npg_GmnX5gWr3QDc;SSL Mode=Require;Trust Server Certificate=true"
}
```

**Opcion C — Editar `appsettings.json` directamente con la password real** (NO recomendado — el archivo esta en git).

### 2.3. Reiniciar la app

Segun tu setup:
- **IIS:** `iisreset` o stop/start del Application Pool
- **Docker:** `docker restart <container>` o `docker compose restart`
- **Servicio Windows:** `Restart-Service <nombre>`
- **Kubernetes:** `kubectl rollout restart deployment/<nombre>`

### 2.4. Verificar que arranco bien

```powershell
# En el log de la app, buscar:
# - "Now listening on: http://..."
# - "Application started"
# - "Business=1, Name=Regi Bazar" (cuando hace el backfill o el seeder)
# - "MercadoPagoAccessToken length=219" (si encripto OK con las mismas keys)
#
# No deberia haber excepciones de "Cannot unprotect" (eso es DataProtection keys,
# ver Paso 5).
```

---

## Paso 3 — Smoke test automatizado (2 min)

### 3.1. Sin autenticacion (los 4 tests publicos)

```powershell
cd C:\Codigos\sellgeneral-api\EntregasApi.Migrator
powershell -ExecutionPolicy Bypass -File smoke-test.ps1 -ApiBaseUrl "https://<url-de-tu-api>"
```

**Esperado:** `4 PASS, 0 FAIL, 2 SKIP` (los SKIP son los autenticados).

Los 4 tests publicos:
1. `GET /api/pedido/{accessToken}` — debe devolver el pedido con items y total
2. `GET /api/driver/{driverToken}` — debe devolver la ruta con deliveries
3. `GET /api/public-tanda/{token}` — debe devolver la tanda
4. `GET /api/pedido/token-invalido` — debe devolver 404, no 500

### 3.2. Con autenticacion (los 7 tests, incluidos login)

```powershell
$env:SMOKE_OWNER_EMAIL = "yazmin_vara@hotmail.com"
$env:SMOKE_OWNER_PASSWORD = "<password-real-de-Regi>"
powershell -ExecutionPolicy Bypass -File smoke-test.ps1 -ApiBaseUrl "https://<url-de-tu-api>"
```

**Esperado:** `6 PASS o 7 PASS, 0 FAIL, 0 SKIP` (los 3 autenticados adicionales pasan).

Tests autenticados:
- 5. `POST /api/auth/login` — devuelve JWT
- 6. `GET /api/business/me` con JWT — devuelve `BusinessId=1, Name=Regi Bazar`
- 7. `GET /api/orders/paged` con JWT — devuelve `total >= 700`

### 3.3. Si algo falla

| Test que falla | Probable causa | Que hacer |
|---|---|---|
| 1-3 (publico) | La app no arranco, o el host no resuelve, o el token no existe en la nueva base | Verificar logs de la app, probar el endpoint con curl, ver Paso 5 si es un error de "Cannot unprotect" |
| 4 (404 invalido) | El endpoint acepta tokens invalidos (bug) | Reportar — no es normal, deberia ser 404 |
| 5 (login) | Password incorrecta, o la cuenta no se migro bien, o el JWT Key de `appsettings.json` no es la misma que la app usaba antes | Verificar logs, ver Paso 5.1 (DataProtection) |
| 6 (business/me) | El BusinessId no se esta pasando bien, o el token JWT no tiene los memberships | Probar con curl: `curl -H "Authorization: Bearer <jwt>" https://<api>/api/business/me` |
| 7 (orders/paged) | La query LINQ asume un esquema viejo, o la paginacion esta rota, o falta `X-Business-Id` | Verificar headers, ver logs |

---

## Paso 4 — Smoke test manual en el navegador (10 min)

Ademas del script, abrir manualmente en el navegador para validar la UI:

### 4.1. Login como Owner

1. Abrir `https://regibazar.com/admin/login` (o tu URL de admin)
2. Login con `yazmin_vara@hotmail.com` + password
3. **Esperado:** redirige al panel

### 4.2. Ver la lista de pedidos

1. En el panel, ir a "Pedidos" o equivalente
2. **Esperado:** ves ~743 pedidos. Filtra por recientes — los ultimos son los mismos que en la base vieja (Id 808, 807, 806, etc.)

### 4.3. Abrir un pedido reciente

1. Click en el pedido 808 (o el mas reciente)
2. **Esperado:** muestra cliente (Gar Cia), 3 items (Sábana plana, Cajón king cuadros, Cajón king verde), total $410
3. **Esperado:** si tiene evidencia, la URL de Cloudinary abre la imagen (no 404)

### 4.4. Link publico de tracking (en incognito)

1. Abrir una ventana incognito
2. Pegar un `AccessToken` real en `https://regibazar.com/pedido/{accessToken}`
3. **Esperado:** muestra el pedido SIN pedir login (es publico)

### 4.5. Link del repartidor (en incognito)

1. Pegar un `DriverToken` real en `https://regibazar.com/repartidor/{driverToken}`
2. **Esperado:** muestra la ruta con los deliveries

### 4.6. Gating por plan (si probas como Driver)

1. Logout del Owner, login como `eduardo.rdz28@hotmail.com` (Driver)
2. **Esperado:** ves el panel de Driver con tus rutas asignadas, no el admin
3. Intenta ir a `/admin/financials` o `/admin/reports` — **esperado:** muro de pago o candado (si no son parte de tu plan)

### 4.7. Cami, Tandas, Raffles (si los usas)

1. Login como Owner, ir a Tandas — **esperado:** ves 6 tandas (incluyendo la de "Tanda #4 Carote Pink")
2. Ir a Sorteos — **esperado:** ves el raffle con 235 entries
3. Probar Cami (si esta habilitado) — **esperado:** responde normal

---

## Paso 5 — Verificar DataProtection (critico si los hosts son distintos)

**Que es:** el `MercadoPagoAccessToken` se encripta con DataProtection de ASP.NET Core. La "llave" de encriptacion vive en el filesystem del usuario que corrio el codigo. Si la app corre en otro host (o con otro usuario), no puede desencriptar.

**Como verificar:**

### 5.1. El test 5 (login) ya da senales

- Si el login **falla con 401** ("Correo o contrasena incorrectos") Y el `PasswordHash` en la base esta bien migrado: la desencriptacion funciona (porque la app puede comparar el hash).
- Si el login **falla con 500** (Internal Server Error) y los logs dicen `CryptographicException` o `Cannot unprotect`: las DataProtection keys NO coinciden.

### 5.2. Verificacion directa

En el host donde corre la app, abrir una sesion de PowerShell y correr:

```powershell
# Si la app expone /api/business/me autenticado y devuelve el token MP desencriptado
# correctamente, todo OK. Si no, hay que copiar las keys.

# Para debuggear: en la base nueva, el token encriptado empieza con "CfDJ8..."
# Si la app lo lee y devuelve el original "APP_USR-...", OK.
# Si devuelve null o lanza excepcion, las keys no coinciden.
```

### 5.3. Si las keys NO coinciden

El migrador guardo las keys en su `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys\` (Windows) o `~/.aspnet/DataProtection-Keys` (Linux). Hay que copiarlas al host de la app:

**Windows:**
```powershell
# En la maquina donde corrio el migrador:
Get-ChildItem $env:LOCALAPPDATA\ASP.NET\DataProtection-Keys

# Copiar la carpeta completa (con todos los archivos .xml) al mismo path
# en el host de la app, con el MISMO USUARIO que corre la app.
# La app debe tener permisos de lectura en esos archivos.
```

**Linux:**
```bash
# En la maquina del migrador:
ls ~/.aspnet/DataProtection-Keys/

# Copiar al mismo path del usuario que corre la app
scp -r ~/.aspnet/DataProtection-Keys/ usuario@app-host:~/.aspnet/
```

**Alternativa si no podes copiar las keys:** re-encriptar el token en la base nueva. Esto se puede hacer con un endpoint admin (no existe; habria que agregarlo) o borrando el token encriptado y que Regi lo meta de nuevo por el flujo de FE-4 / panel de marca.

---

## Paso 6 — Avisar a Regi (1 min)

Mensaje sugerido (copialo y mandalo por WhatsApp):

> Hola Regi, ya quedo la migracion a la nueva base. La app sigue funcionando igual pero ahora cada cliente y cada pedido tiene un "tenant" interno. **No tenes que hacer nada** — solo avisame si ves algo raro: pedidos que no aparecen, links de tracking rotos, o errores 500. Yo monitoreo por 24-72h.

---

## Paso 7 — Monitoreo pasivo (24-72h)

Lo que NO requiere accion tuya pero conviene que mires de reojo:

- [ ] **Latencia de queries:** deberia ser igual o mejor que antes (la nueva DB es la misma infra Neon)
- [ ] **Errores 5xx en logs:** cualquier pico inusual
- [ ] **402 `subscription_locked`:** NO deberian aparecer (Regi esta `PlanTier=Elite, SubscriptionStatus=Active`)
- [ ] **500 en `/api/driver/{driverToken}` o `/api/pedido/{accessToken}`:** estos son los endpoints que usan las clientas; cualquier error aqui es urgente
- [ ] **Webhooks de Mercado Pago:** si Regi recibe pagos, los webhooks de MP deberian seguir llegando a la app correctamente. La suscripcion de Regi (1.3) usa credenciales de plataforma distintas del token per-tenant que acabamos de migrar.

---

## Paso 8 — Limpieza de archivos y seguridad (10 min)

### 8.1. Mover `connectionStrings.txt` fuera del repo

Este archivo contiene passwords de las 3 bases. **No debe quedar en git.** Opciones:

**Opcion A — Agregarlo al .gitignore y dejarlo en el repo local:**
```
# En C:\Codigos\sellgeneral-api\.gitignore, agregar:
connectionStrings.txt
```

**Opcion B — Moverlo fuera del repo y nunca volverlo a poner:**
```powershell
Move-Item C:\Codigos\sellgeneral-api\connectionStrings.txt C:\Users\eduardo.rdz\.credentials\
```

**Opcion C — Moverlo a un vault** (si usas 1Password, Bitwarden, etc.)

### 8.2. Limpiar snapshots del migrador

Despues de confirmar que todo esta OK (Paso 3 + Paso 4):

```powershell
Remove-Item C:\Codigos\sellgeneral-api\EntregasApi.Migrator\.snapshot_*.txt
```

### 8.3. NO borrar el migrador todavia

El proyecto `EntregasApi.Migrator/` es un activo del repo. Aunque ya no se va a volver a correr, es documentacion viva de como se hizo la migracion. Lo dejo en el repo, eventualmente se puede archivar o mover a un repo separado.

### 8.4. Validar el README del migrador

`C:\Codigos\sellgeneral-api\EntregasApi.Migrator\README.md` ya esta escrito. Si hiciste cambios al flujo (ej. decidiste no usar `--evidence-map`), actualizalo para que refleje la realidad.

---

## Paso 9 — Despues de 30 dias: archivar la base vieja (10 min)

**Regla:** la base vieja (`ep-steep-bar-ai7vx9g2...c-4`) se mantiene accesible por 30 dias como red de seguridad. Pasado ese tiempo:

### 9.1. Confirmar que prod-cutover es estable

- [ ] 30 dias sin incidentes graves
- [ ] Regi opera normal
- [ ] Smoke test sigue pasando

### 9.2. Opciones para la base vieja

**Opcion A — Archivar en Neon:** cambiar el tier del branch a "archived" (Neon cobra menos por branches archivados). No perder los datos pero dejar de pagar.

**Opcion B — Snapshot final y borrar:** crear un dump final de la base vieja, guardarlo en S3/Cloudinary/disco, y luego eliminar el branch. Mas radical pero mas limpio.

**Opcion C — Dejar como esta:** Neon cobra poco por branches sin uso, podes dejarla meses. La red de seguridad maxima.

**Recomendacion:** Opcion A. Si en algun momento futuro necesitas datos historicos de la era single-tenant, los podes recuperar; pero no estas pagando CPU/RAM por una base que no se usa.

### 9.3. Limpiar referencias en el repo

Una vez archivada la base vieja:

- [ ] Actualizar `appsettings.json` (quitar referencias al host viejo)
- [ ] Actualizar `M.1-RUNBOOK.md` (marcar la corrida como completada)
- [ ] Actualizar `MIGRACION-DIAGNOSTICO.md` (anotar que la migracion se completo en fecha X)

---

## Paso 10 — Opcional: campana de captura de telefonos (no bloqueante)

**Contexto:** el diagnostico encontro que 95% de las clientas no tienen telefono registrado. Esto es independiente de la migracion, pero ahora que la nueva base esta en produccion, es buen momento para:

- [ ] Crear un endpoint o vista en el panel admin para que Regi capture telefonos rapidamente
- [ ] O: enviar un mensaje broadcast a las clientas top pidiéndoles que actualicen su contacto
- [ ] O: cuando Regi interactue con una clienta, capturar el telefono en el momento

Esto NO bloquea el cierre de la migracion. Es trabajo futuro.

---

## Resumen de tiempos

| Paso | Tiempo | Bloqueante? |
|---|---|---|
| 1. Verificar pre-condiciones | 5 min | si |
| 2. Apuntar app + reiniciar | 2-15 min | si |
| 3. Smoke test automatizado | 2 min | si |
| 4. Smoke test manual navegador | 10 min | muy recomendado |
| 5. Verificar DataProtection | 5 min | critico si los hosts son distintos |
| 6. Avisar a Regi | 1 min | si (sin esto no cierra) |
| 7. Monitoreo 24-72h | pasivo | no (pero miralo) |
| 8. Limpieza archivos | 10 min | muy recomendado |
| 9. Archivar base vieja (30 dias) | 10 min | no (en 30 dias) |
| 10. Campana telefonos | horas | no (futuro) |

**Total para cerrar la migracion:** ~30 min de trabajo activo + monitoreo pasivo.

---

## Si algo sale mal — Rollback

**Rollback express (5 min):**

1. Cambiar la env var o `appsettings.json` de vuelta al host viejo (`ep-steep-bar-...c-4`)
2. Reiniciar la app
3. Verificar con login + un pedido cualquiera

**Lo que pasa:** la app vuelve a leer de la base single-tenant vieja. La base `prod-cutover` queda intacta (podes re-intentar mas tarde o abandonarla).

**Lo que NO se rompe:** la base vieja sigue 100% intacta (probado con 16/16 snapshots antes y despues). Puedes volver a ella cuando quieras.

**Cuando investigar:** el detalle del FAIL en el smoke test o el mensaje de error en los logs de la app.
