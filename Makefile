.PHONY: help build build-release test coverage clean pack setup bump-major bump-minor bump-patch all

# Variables
VERSION := $(shell cat version)
NUPKG_DIR := ./nupkgs

help: ## Show this help message
	@echo 'Usage: make [target]'
	@echo ''
	@echo 'Available targets:'
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-20s %s\n", $$1, $$2}'

build: ## Build the solution in Debug mode
	dotnet build Espresso.sln -c Debug

build-release: ## Build the solution in Release mode
	dotnet build Espresso.sln -c Release

test: ## Run all tests
	dotnet test Espresso.sln

coverage: ## Run tests with coverage report
	dotnet test Espresso.sln \
		/p:CollectCoverage=true \
		/p:CoverletOutputFormat=cobertura \
		/p:CoverletOutput=./TestResults/

clean: ## Clean build artifacts
	dotnet clean Espresso.sln || true
	rm -rf $(NUPKG_DIR)
	rm -rf */*/bin */*/obj */bin */obj

pack: clean build-release ## Pack NuGet package
	dotnet pack src/Espresso/Espresso.csproj -c Release -o $(NUPKG_DIR)
	@echo ""
	@echo "Package created: $(NUPKG_DIR)/Espresso.Caching.$(VERSION).nupkg"

setup: ## Initial setup (restore packages)
	dotnet restore

bump-major: ## Bump major version (1.0.0 -> 2.0.0)
	@echo "Current version: $(VERSION)"
	@echo $(VERSION) | awk -F. '{print $$1+1".0.0"}' > version
	@echo "New version: $$(cat version)"

bump-minor: ## Bump minor version (1.0.0 -> 1.1.0)
	@echo "Current version: $(VERSION)"
	@echo $(VERSION) | awk -F. '{print $$1"."$$2+1".0"}' > version
	@echo "New version: $$(cat version)"

bump-patch: ## Bump patch version (1.0.0 -> 1.0.1)
	@echo "Current version: $(VERSION)"
	@echo $(VERSION) | awk -F. '{print $$1"."$$2"."$$3+1}' > version
	@echo "New version: $$(cat version)"

all: clean build test ## Clean, build, and test
