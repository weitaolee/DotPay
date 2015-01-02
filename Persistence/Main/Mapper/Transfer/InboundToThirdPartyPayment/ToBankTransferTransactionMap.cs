﻿using DotPay.MainDomain;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using DotPay.Common;

namespace DotPay.Persistence
{
    public class ToBankTransferTransactionMap : BaseClassMapping<ToBankTransferTransaction>
    {
        public ToBankTransferTransactionMap()
        {
            Id(u => u.ID, map => map.Generator(Generators.Identity));
            Property(x => x.SequenceNo, map => { map.NotNullable(true); map.Unique(true); map.Length(40); });
            Property(x => x.TxId, map => { map.NotNullable(true); map.Length(65); map.Unique(true); });
            Property(x => x.PayWay, map => { map.NotNullable(true); });
            Property(x => x.SourcePayWay, map => { map.NotNullable(true); });
            Property(x => x.Account, map => { map.NotNullable(true); });
            Property(x => x.Amount, map => { map.NotNullable(true); map.Precision(12); map.Scale(4); });
            Property(x => x.TransferPayWay, map => { map.NotNullable(true); });
            Property(x => x.TransferNo, map => { map.NotNullable(true); map.Length(30); });
            Property(x => x.State, map => { map.NotNullable(true); });
            Property(x => x.CreateAt, map => { map.NotNullable(true); });
            Property(x => x.DoneAt, map => { map.NotNullable(true); });
            Property(x => x.OperatorID, map => { map.NotNullable(true); });
            Property(x => x.RealName, map => { map.NotNullable(true); map.Length(20); });
            Property(x => x.Memo, map => { map.NotNullable(true); });
            Property(x => x.Reason, map => { map.NotNullable(true); });
            Version(x => x.Version, map => { });
        }
    }
}