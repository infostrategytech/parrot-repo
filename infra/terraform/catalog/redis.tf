resource "aws_elasticache_subnet_group" "catalog" {
  name       = local.name
  subnet_ids = var.private_subnet_ids
}

resource "aws_security_group" "redis" {
  name   = "${local.name}-redis"
  vpc_id = var.vpc_id
}

resource "aws_elasticache_replication_group" "catalog" {
  replication_group_id       = local.name
  description                = "Catalog read cache (tenant-prefixed keys, generation counters)"
  engine                     = "redis"
  engine_version             = "7.1"
  node_type                  = var.redis_node_type
  num_cache_clusters         = 2
  automatic_failover_enabled = true
  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  auth_token                 = random_password.redis.result
  subnet_group_name          = aws_elasticache_subnet_group.catalog.name
  security_group_ids         = [aws_security_group.redis.id]
  parameter_group_name       = "default.redis7"
}

resource "random_password" "redis" {
  length  = 48
  special = false
}
