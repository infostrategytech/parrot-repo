resource "aws_ecs_cluster" "catalog" {
  name = local.name
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

resource "aws_security_group" "tasks" {
  name   = "${local.name}-tasks"
  vpc_id = var.vpc_id
}

resource "aws_security_group_rule" "db_from_tasks" {
  type                     = "ingress"
  from_port                = 5432
  to_port                  = 5432
  protocol                 = "tcp"
  security_group_id        = aws_security_group.db.id
  source_security_group_id = aws_security_group.tasks.id
}

resource "aws_security_group_rule" "redis_from_tasks" {
  type                     = "ingress"
  from_port                = 6379
  to_port                  = 6379
  protocol                 = "tcp"
  security_group_id        = aws_security_group.redis.id
  source_security_group_id = aws_security_group.tasks.id
}

resource "aws_cloudwatch_log_group" "catalog" {
  name              = "/ecs/${local.name}"
  retention_in_days = 30
}

locals {
  common_env = [
    { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
    { name = "Catalog__Cache__Provider", value = "Redis" },
    { name = "Catalog__Relay__BootstrapServers", value = var.kafka_bootstrap_servers },
    { name = "Catalog__Relay__Topic", value = "catalog.events" },
    { name = "Catalog__Relay__ItemsTopic", value = "catalog.items" },
    { name = "Catalog__Relay__IngestionReportTopic", value = "catalog.ingestion_report" },
    { name = "JwtSettings__Issuer", value = var.jwt_issuer },
    { name = "JwtSettings__Audience", value = var.jwt_audience },
    { name = "OpenTelemetry__Endpoint", value = var.otlp_endpoint },
    # Database access is IAM: Username only, the token is fetched at connect time.
    { name = "Catalog__ConnectionStrings__Writer", value = "Host=${aws_rds_cluster.catalog.endpoint};Database=${var.db_name};Username=catalog_writer_app;SSL Mode=VerifyFull" },
    { name = "Catalog__ConnectionStrings__Reader", value = "Host=${aws_rds_cluster.catalog.reader_endpoint};Database=${var.db_name};Username=catalog_reader_app;SSL Mode=VerifyFull" },
  ]
}

resource "aws_ecs_task_definition" "api" {
  family                   = "${local.name}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  task_role_arn            = aws_iam_role.api_task.arn
  execution_role_arn       = aws_iam_role.api_task.arn # TODO: separate execution role with ECR + logs only

  container_definitions = jsonencode([{
    name                   = "api"
    image                  = var.api_image
    essential              = true
    user                   = "1654" # $APP_UID of the aspnet image
    readonlyRootFilesystem = true
    portMappings           = [{ containerPort = 8080, protocol = "tcp" }]
    environment = concat(local.common_env, [
      { name = "Catalog__ConnectionStrings__Migrator", value = "Host=${aws_rds_cluster.catalog.endpoint};Database=${var.db_name};Username=catalog_migrator;SSL Mode=VerifyFull" },
      { name = "Catalog__ApplyMigrations", value = "false" }, # migrations run as a one-off task, not on every boot
    ])
    secrets = [
      { name = "JwtSettings__Secret", valueFrom = aws_secretsmanager_secret.jwt.arn },
      { name = "Catalog__Cache__RedisConnectionString", valueFrom = aws_secretsmanager_secret.redis_auth.arn }, # TODO: compose "host:6380,ssl=true,password=..."
    ]
    healthCheck = { command = ["CMD-SHELL", "curl -fsS http://localhost:8080/health || exit 1"], interval = 30, retries = 3, timeout = 5 }
    logConfiguration = {
      logDriver = "awslogs"
      options   = { awslogs-group = aws_cloudwatch_log_group.catalog.name, awslogs-region = var.region, awslogs-stream-prefix = "api" }
    }
  }])
}

resource "aws_ecs_task_definition" "relay" {
  family                   = "${local.name}-relay"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  task_role_arn            = aws_iam_role.relay_task.arn
  execution_role_arn       = aws_iam_role.relay_task.arn

  container_definitions = jsonencode([{
    name                   = "relay"
    image                  = var.relay_image
    essential              = true
    user                   = "1654"
    readonlyRootFilesystem = true
    environment = concat(local.common_env, [
      { name = "DOTNET_ENVIRONMENT", value = "Production" },
      { name = "Catalog__ConnectionStrings__Relay", value = "Host=${aws_rds_cluster.catalog.endpoint};Database=${var.db_name};Username=catalog_relay_app;SSL Mode=VerifyFull" },
    ])
    secrets = [
      { name = "Catalog__Cache__RedisConnectionString", valueFrom = aws_secretsmanager_secret.redis_auth.arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options   = { awslogs-group = aws_cloudwatch_log_group.catalog.name, awslogs-region = var.region, awslogs-stream-prefix = "relay" }
    }
  }])
}

resource "aws_ecs_service" "api" {
  name            = "${local.name}-api"
  cluster         = aws_ecs_cluster.catalog.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = 2
  launch_type     = "FARGATE"
  network_configuration {
    subnets         = var.private_subnet_ids
    security_groups = [aws_security_group.tasks.id]
  }
  # TODO: load_balancer { target_group_arn = ..., container_name = "api", container_port = 8080 }
}

resource "aws_ecs_service" "relay" {
  name            = "${local.name}-relay"
  cluster         = aws_ecs_cluster.catalog.id
  task_definition = aws_ecs_task_definition.relay.arn
  desired_count   = 1
  launch_type     = "FARGATE"
  network_configuration {
    subnets         = var.private_subnet_ids
    security_groups = [aws_security_group.tasks.id]
  }
}

# Alerts the runbook expects: outbox lag and RLS errors (metrics exported via OTLP).
# TODO: aws_cloudwatch_metric_alarm on catalog.outbox.lag_seconds > 60 and catalog.rls.errors > 0.
