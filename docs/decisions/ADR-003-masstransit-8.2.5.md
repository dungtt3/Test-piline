# ADR-003: Dùng MassTransit 8.2.5 thay vì 8.3.x

- **Bối cảnh:** MassTransit 8.3.x phụ thuộc RabbitMQ.Client 7.x, trong khi AspNetCore.HealthChecks.Rabbitmq 8.0.2 yêu cầu RabbitMQ.Client 6.x (API v7 đổi hoàn toàn, bind lên 7 sẽ vỡ runtime).
- **Quyết định:** Pin MassTransit.RabbitMQ 8.2.5 (dùng RabbitMQ.Client 6.8.1) để cùng version với health check package.
- **Hệ quả:** Không có breaking change nào ảnh hưởng Phase 1; nâng cấp lên 8.3+ khi health check package hỗ trợ RabbitMQ.Client 7.
