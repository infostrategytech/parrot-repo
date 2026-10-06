resource "aws_kms_key" "db" {
  description         = "${local.name} Aurora at-rest encryption"
  enable_key_rotation = true
}

resource "aws_rds_cluster_parameter_group" "catalog" {
  name   = "${local.name}-pg16"
  family = "aurora-postgresql16"

  # TLS is mandatory; every client must connect with SSL Mode=VerifyFull.
  parameter {
    name  = "rds.force_ssl"
    value = "1"
  }

  parameter {
    name  = "log_min_duration_statement"
    value = "1000"
  }
}

resource "aws_db_subnet_group" "catalog" {
  name       = local.name
  subnet_ids = var.private_subnet_ids
}

resource "aws_security_group" "db" {
  name   = "${local.name}-db"
  vpc_id = var.vpc_id
  # Ingress 5432 only from the ECS tasks' security group (see ecs.tf).
}

resource "aws_rds_cluster" "catalog" {
  cluster_identifier              = local.name
  engine                          = "aurora-postgresql"
  engine_version                  = "16.4" # TODO: pin to the version pgvector is available on
  database_name                   = var.db_name
  master_username                 = "parrot_master"
  manage_master_user_password     = true
  iam_database_authentication_enabled = true
  storage_encrypted               = true
  kms_key_id                      = aws_kms_key.db.arn
  db_subnet_group_name            = aws_db_subnet_group.catalog.name
  vpc_security_group_ids          = [aws_security_group.db.id]
  db_cluster_parameter_group_name = aws_rds_cluster_parameter_group.catalog.name
  backup_retention_period         = 14
  deletion_protection             = true
  copy_tags_to_snapshot           = true

  serverlessv2_scaling_configuration {
    min_capacity = 0.5
    max_capacity = 8
  }
}

resource "aws_rds_cluster_instance" "catalog" {
  count              = 2
  identifier         = "${local.name}-${count.index}"
  cluster_identifier = aws_rds_cluster.catalog.id
  instance_class     = var.db_instance_class
  engine             = aws_rds_cluster.catalog.engine
  engine_version     = aws_rds_cluster.catalog.engine_version
  performance_insights_enabled = true
}
