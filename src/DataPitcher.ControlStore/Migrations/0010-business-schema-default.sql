UPDATE ConnectionProfiles SET BusinessSchema = CASE WHEN ProviderId = 'postgresql' THEN 'public' ELSE 'dbo' END WHERE BusinessSchema = 'app';
