output "db_writer_endpoint" {
  value = aws_rds_cluster.catalog.endpoint
}

output "db_reader_endpoint" {
  value = aws_rds_cluster.catalog.reader_endpoint
}

output "redis_primary_endpoint" {
  value = aws_elasticache_replication_group.catalog.primary_endpoint_address
}

output "ecs_cluster" {
  value = aws_ecs_cluster.catalog.name
}
