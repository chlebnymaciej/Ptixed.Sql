﻿using Ptixed.Sql.Metadata;
using System.Collections.Generic;
using System.Linq;

namespace Ptixed.Sql.Implementation.Trackers
{
    internal class TransactionalTracker : ITracker
    {
        private readonly List<(Table table, object id)> _deletes = new List<(Table table, object id)>();

        private readonly Dictionary<(Table, object), object> _commited = new Dictionary<(Table, object), object>();
        private readonly Dictionary<(Table, object), object> _uncommited = new Dictionary<(Table, object), object>();

        public object Get(Table table, object id)
        {
            return _commited.TryGetValue((table, id), out object ret) ? ret : null;
        }

        public void Set(Table table, object id, object entity)
        {
            _commited[(table, id)] = entity;
            _uncommited[(table, id)] = entity;
        }

        public void ScheduleDelete(Table type, object id) => _deletes.Add((type, id));

        public void ScheduleUpdate(object entity)
        {
            var table = Table.Get(entity.GetType());
            var pk = table[entity, table.PrimaryKey];
            _uncommited[(table, pk)] = entity;
        }

        public List<Query> Flush()
        {
            var queries = new List<Query>();
            queries.Add(new Query($"SET NOCOUNT ON"));

            queries.AddRange(FlushDeletes());
            queries.AddRange(FlushUpdates());
            if (queries.Count == 1)
                return new List<Query>();

            queries.Add(new Query($"SET NOCOUNT OFF"));
            return queries;
        }

        private IEnumerable<Query> FlushDeletes()
        {
            foreach (var delete in _deletes)
            {
                yield return QueryBuilder.Delete(delete.table, delete.id);
                if (_commited.Remove(delete))
                    _uncommited.Remove(delete);
            }
            _deletes.Clear();
        }

        private IEnumerable<Query> FlushUpdates()
        {
            foreach (var (key, entity) in _uncommited)
            {
                var (table, id) = key;
                var old = _commited[key];

                if (old == null)
                    yield return QueryBuilder.Update(entity);
                else
                {
                    var columns = new List<LogicalColumn>();
                    foreach (var column in table.LogicalColumns)
                        if (!Equals(table[entity, column], table[old, column]))
                            columns.Add(column);

                    if (!columns.Any())
                        continue;

                    var values = columns
                        .SelectMany(column => column.FromEntityToQuery(entity))
                        .Select(column => new Query($"{column} = {column.Value}"))
                        .ToList();

                    // pk updates

                    var query = new Query();
                    query.Append($"UPDATE {table} SET ");
                    query.Append($", ", values);
                    query.Append($" WHERE ");
                    query.Append(table.GetPrimaryKeyCondition(id));
                    yield return query;
                }

                _commited[key] = table.Clone(entity);
            }
        }
    }
}
