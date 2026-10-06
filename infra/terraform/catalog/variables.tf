variable "region" {
  type    = string
  default = "eu-west-1" # TODO
}

variable "environment" {
  type = string
}

variable "vpc_id" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "api_image" {
  type        = string
  description = "ECR image for Parrot.Catalog.Api, pinned by digest."
}

variable "relay_image" {
  type        = string
  description = "ECR image for Parrot.Catalog.Relay, pinned by digest."
}

variable "db_name" {
  type    = string
  default = "parrot"
}

variable "db_instance_class" {
  type    = string
  default = "db.serverless"
}

variable "redis_node_type" {
  type    = string
  default = "cache.t4g.small"
}

variable "kafka_bootstrap_servers" {
  type        = string
  description = "MSK bootstrap brokers (SASL/IAM)."
}

variable "jwt_issuer" {
  type    = string
  default = "ParrotApi"
}

variable "jwt_audience" {
  type    = string
  default = "ParrotApp"
}

variable "otlp_endpoint" {
  type    = string
  default = ""
}
