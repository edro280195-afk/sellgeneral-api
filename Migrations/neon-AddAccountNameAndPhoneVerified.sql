-- ============================================================================
-- Migración: AddAccountNameAndPhoneVerified (20260706150030)
-- Agrega a la tabla "Accounts": FirstName, LastName y PhoneVerifiedAt.
--
-- ⚠️ NORMALMENTE NO NECESITAS EJECUTAR ESTO A MANO.
-- La API corre `db.Database.MigrateAsync()` al arrancar (Program.cs), así que
-- Render aplica esta migración automáticamente en el próximo deploy.
--
-- Ejecuta este script en Neon SOLO si quieres aplicarlo manualmente ANTES del
-- deploy. Incluye el registro en "__EFMigrationsHistory" para que la API no
-- intente volver a aplicarla (evita el error "column already exists").
-- ============================================================================

START TRANSACTION;

ALTER TABLE "Accounts" ADD "FirstName" character varying(100);

ALTER TABLE "Accounts" ADD "LastName" character varying(100);

ALTER TABLE "Accounts" ADD "PhoneVerifiedAt" timestamp with time zone;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260706150030_AddAccountNameAndPhoneVerified', '8.0.11');

COMMIT;
