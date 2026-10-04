# Secrets live here and reach the tasks as ECS secrets, never as plain environment or in images.
resource "aws_secretsmanager_secret" "jwt" {
  name       = "${local.name}/jwt-signing-key"
  kms_key_id = aws_kms_key.db.arn
}

resource "aws_secretsmanager_secret" "redis_auth" {
  name       = "${local.name}/redis-auth-token"
  kms_key_id = aws_kms_key.db.arn
}

resource "aws_secretsmanager_secret_version" "redis_auth" {
  secret_id     = aws_secretsmanager_secret.redis_auth.id
  secret_string = random_password.redis.result
}
