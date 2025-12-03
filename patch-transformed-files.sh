#!/bin/bash
# Patch Stage: Fix remaining varchar IDs in already-transformed files
# Operates on 04_*.transformed.sql files to replace any missed varchar FKs

cd /mnt/HC_Volume_101891352/migration

echo "Patching transformed files with complete mapping..."
echo

# Export id_mapping to temp file for quick lookup
mysql -u espocrm_migration -p26bdb99a4e9eed595920 -N -e "SELECT old_id, new_id FROM espocrm_migration.id_mapping" > /tmp/id_mapping.tsv

# Build associative array for lookups
declare -A mapping
while IFS=$'\t' read -r old_id new_id; do
    mapping["$old_id"]="$new_id"
done < /tmp/id_mapping.tsv

echo "Loaded ${#mapping[@]} mappings"
echo

# Process each transformed file
for file in 04_*.transformed.sql; do
    echo "Patching $file..."

    # Find remaining varchar IDs
    remaining=$(grep -oP "'[0-9a-f]{17}'" "$file" | sort -u | wc -l)

    if [ $remaining -eq 0 ]; then
        echo "  No varchar IDs remaining, skipping"
        continue
    fi

    echo "  Found $remaining unique varchar IDs to replace"

    # Create temp patched file
    cp "$file" "$file.tmp"

    # Replace each varchar ID
    grep -oP "'[0-9a-f]{17}'" "$file" | sort -u | while read -r quoted_id; do
        old_id="${quoted_id:1:17}"  # Remove quotes
        new_id="${mapping[$old_id]}"

        if [ -n "$new_id" ]; then
            sed -i "s/'$old_id'/'$new_id'/g" "$file.tmp"
        fi
    done

    mv "$file.tmp" "$file"
    echo "  ✓ Patched"
done

echo
echo "Patch complete!"
