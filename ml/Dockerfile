FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN pip install --no-cache-dir "numpy>=1.26" "lightgbm>=4.3" "fastapi>=0.115" "uvicorn>=0.30"
COPY aerobag_ml ./aerobag_ml
COPY service ./service
COPY models ./models
RUN useradd -r -u 10001 app
USER app
ENV AEROBAG_MODEL_DIR=/app/models
EXPOSE 8000
CMD ["uvicorn", "service.app:app", "--host", "0.0.0.0", "--port", "8000"]
