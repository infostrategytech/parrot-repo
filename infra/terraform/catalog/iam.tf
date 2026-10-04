data "aws_iam_policy_document" "task_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "api_task" {
  name               = "${local.name}-api-task"
  assume_role_policy = data.aws_iam_policy_document.task_assume.json
}

resource "aws_iam_role" "relay_task" {
  name               = "${local.name}-relay-task"
  assume_role_policy = data.aws_iam_policy_document.task_assume.json
}

# RDS IAM auth: each task may connect only as its own database users.
data "aws_iam_policy_document" "api_db" {
  statement {
    actions = ["rds-db:connect"]
    resources = [
      "arn:aws:rds-db:${var.region}:${data.aws_caller_identity.current.account_id}:dbuser:${aws_rds_cluster.catalog.cluster_resource_id}/catalog_writer_app",
      "arn:aws:rds-db:${var.region}:${data.aws_caller_identity.current.account_id}:dbuser:${aws_rds_cluster.catalog.cluster_resource_id}/catalog_reader_app",
      "arn:aws:rds-db:${var.region}:${data.aws_caller_identity.current.account_id}:dbuser:${aws_rds_cluster.catalog.cluster_resource_id}/catalog_migrator",
    ]
  }
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.jwt.arn, aws_secretsmanager_secret.redis_auth.arn]
  }
}

data "aws_iam_policy_document" "relay_db" {
  statement {
    actions = ["rds-db:connect"]
    resources = [
      "arn:aws:rds-db:${var.region}:${data.aws_caller_identity.current.account_id}:dbuser:${aws_rds_cluster.catalog.cluster_resource_id}/catalog_relay_app",
    ]
  }
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.redis_auth.arn]
  }
  # TODO: kafka-cluster:Connect / WriteData / ReadData on the MSK cluster and the catalog.events topic.
}

resource "aws_iam_role_policy" "api_db" {
  role   = aws_iam_role.api_task.id
  policy = data.aws_iam_policy_document.api_db.json
}

resource "aws_iam_role_policy" "relay_db" {
  role   = aws_iam_role.relay_task.id
  policy = data.aws_iam_policy_document.relay_db.json
}
