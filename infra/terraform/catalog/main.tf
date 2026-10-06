terraform {
  required_version = ">= 1.6"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.60"
    }
  }
  # TODO: backend "s3" { bucket = "...", key = "catalog/terraform.tfstate", region = "...", dynamodb_table = "..." }
}

provider "aws" {
  region = var.region
  default_tags {
    tags = {
      Service     = "parrot-catalog"
      Environment = var.environment
      ManagedBy   = "terraform"
    }
  }
}

data "aws_caller_identity" "current" {}

locals {
  name = "parrot-catalog-${var.environment}"
}
