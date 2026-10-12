# 통합 테스트용 인프라

RabbitMQ 3.13(5672, 관리 15672) · Redis 7.2(6379) · PostgreSQL 16(5432).

```
cp .env.example .env    # 값을 채운다
docker compose up -d
docker compose ps
```
