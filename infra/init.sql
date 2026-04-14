CREATE TABLE spells (
  id SERIAL PRIMARY KEY,
  spell_type TEXT,
  power INT,
  result TEXT,
  damage INT,
  created_at TIMESTAMP DEFAULT NOW()
);
